using System.Text;
using System.Text.Json;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class ChatEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/chat", async (ChatRequest req,
            IAgentService agentService,
            IConfiguration config,
            AppDbContext db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.BookSlug) || req.BookSlug.Contains("..") || req.BookSlug.Contains('/') || req.BookSlug.Contains('\\'))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid book slug" }, ct);
                return;
            }

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == req.BookSlug, ct);

            var response = ctx.Response;
            response.ContentType = "text/event-stream";
            response.Headers.Append("Cache-Control", "no-cache");
            response.Headers.Append("Connection", "keep-alive");

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var wikiDir = Path.Combine(libraryPath, req.BookSlug, "wiki");
            var chaptersDir = Path.Combine(libraryPath, req.BookSlug, "chapters");

            // ── Build context ──────────────────────────────────────────────
            var contextParts = new List<string>();

            if (Directory.Exists(chaptersDir))
            {
                var chapterFiles = Directory.GetFiles(chaptersDir, "ch-*.md")
                    .Where(f => !f.EndsWith(".scratch.md"))
                    .OrderBy(f => f)
                    .ToArray();

                if (chapterFiles.Length > 0)
                {
                    var chapterList = new StringBuilder("# Chapters\n\n");
                    for (int i = 0; i < chapterFiles.Length; i++)
                    {
                        var chContent = await File.ReadAllTextAsync(chapterFiles[i], ct);
                        var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? $"Chapter {i + 1}";
                        chapterList.AppendLine($"{i + 1}. {title} ({Path.GetFileName(chapterFiles[i])})");
                    }
                    contextParts.Add(chapterList.ToString());
                }

                // Referenced chapters (by name/number in user message)
                if (!string.IsNullOrEmpty(req.Message) && chapterFiles.Length > 0)
                {
                    var msgLower = req.Message.ToLowerInvariant();
                    foreach (var chFile in chapterFiles)
                    {
                        var chContent = await File.ReadAllTextAsync(chFile, ct);
                        var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "";
                        var fileName = Path.GetFileNameWithoutExtension(chFile);

                        if (fileName == req.ActiveChapterId) continue;

                        var match = false;
                        if (!string.IsNullOrEmpty(title) && msgLower.Contains(title.ToLowerInvariant()))
                            match = true;
                        if (int.TryParse(msgLower.Replace("chapter", "").Trim(), out var chNum))
                        {
                            var idx = Array.IndexOf(chapterFiles, chFile);
                            if (idx == chNum - 1) match = true;
                        }

                        if (match)
                        {
                            var truncated = chContent.Length > 15_000
                                ? chContent[..15_000] + "\n[... truncated ...]"
                                : chContent;
                            contextParts.Add($"# Referenced Chapter: {title}\n\n{truncated}");
                        }
                    }
                }
            }

            // Active chapter
            if (!string.IsNullOrEmpty(req.ActiveChapterId) && Directory.Exists(chaptersDir))
            {
                var activeFile = Path.Combine(chaptersDir, $"{req.ActiveChapterId}.md");
                if (File.Exists(activeFile))
                {
                    var activeChapterContent = await File.ReadAllTextAsync(activeFile, ct);
                    var activeChapterTitle = activeChapterContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? req.ActiveChapterId;
                    var truncated = activeChapterContent.Length > 30_000
                        ? activeChapterContent[..30_000] + "\n\n[... truncated ...]"
                        : activeChapterContent;
                    contextParts.Add($"# Current Chapter: {activeChapterTitle}\n\n{truncated}");
                }
            }

            // Wiki files
            if (Directory.Exists(wikiDir))
            {
                foreach (var wikiFile in Directory.GetFiles(wikiDir, "*.md").OrderBy(f => f))
                {
                    var wikiContent = await File.ReadAllTextAsync(wikiFile, ct);
                    if (wikiContent.Length > 10_000)
                        wikiContent = wikiContent[..10_000] + "\n\n[... truncated ...]";
                    contextParts.Add($"# Wiki: {Path.GetFileName(wikiFile)}\n\n{wikiContent}");
                }
            }

            var context = string.Join("\n\n---\n\n", contextParts);

            // ── Agent session ──────────────────────────────────────────────
            string sessionId;

            if (!string.IsNullOrEmpty(req.SessionId))
            {
                sessionId = req.SessionId;
            }
            else
            {
                sessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
            }

            // Inject context on fresh sessions
            try
            {
                var info = await agentService.GetSessionInfoAsync(sessionId, ct);
                if (info.MessageCount == 0)
                {
                    var contextPrompt = new StringBuilder()
                        .AppendLine("You are an AI assistant helping with a book. You can answer questions about the story, characters, and themes using the context below.")
                        .AppendLine()
                        .AppendLine("You also have the ability to modify story content. When the user explicitly asks you to edit, rewrite, modify, or create content, write the complete modified file to the path they specify. Do NOT modify files unless the user explicitly asks.")
                        .AppendLine()
                        .AppendLine(context)
                        .ToString();
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
            }
            catch
            {
                try
                {
                    await agentService.SendPromptAsync(sessionId, $"You are helping with a book.\n\n{context}", ct);
                }
                catch { }
            }

            // ── Save user message ──────────────────────────────────────────
            if (book is not null)
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    BookId = book.Id,
                    Role = "user",
                    Content = req.Message,
                    SessionId = sessionId,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // ── Stream response ────────────────────────────────────────────
            var assistantText = "";
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, req.Message, ct))
            {
                await response.WriteAsync($"data: {evt}\n\n", ct);
                await response.Body.FlushAsync(ct);

                try
                {
                    var parsed = JsonDocument.Parse(evt);
                    if (parsed.RootElement.TryGetProperty("type", out var type) && type.GetString() == "message_update")
                    {
                        if (parsed.RootElement.TryGetProperty("assistantMessageEvent", out var asm)
                            && asm.TryGetProperty("type", out var asmType) && asmType.GetString() == "text_delta"
                            && asm.TryGetProperty("delta", out var delta))
                        {
                            assistantText += delta.GetString() ?? "";
                        }
                    }
                }
                catch { }
            }

            // ── Check for scratch file ─────────────────────────────────────
            if (!string.IsNullOrEmpty(req.ActiveChapterId))
            {
                var scratchFile = Path.Combine(libraryPath, req.BookSlug, "chapters", $"{req.ActiveChapterId}.scratch.md");
                if (File.Exists(scratchFile))
                {
                    var doneEvent = JsonSerializer.Serialize(new
                    {
                        type = "edit_done",
                        chapterId = req.ActiveChapterId,
                        scratchPath = $"chapters/{req.ActiveChapterId}.scratch.md",
                        source = "chat"
                    });
                    await response.WriteAsync($"data: {doneEvent}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }

            // ── Save assistant response ────────────────────────────────────
            if (book is not null && !string.IsNullOrEmpty(assistantText))
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    BookId = book.Id,
                    Role = "assistant",
                    Content = assistantText,
                    SessionId = sessionId,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // ── Return session ID to frontend ─────────────────────────────
            var sessionEvent = JsonSerializer.Serialize(new { type = "session_info", sessionId });
            await response.WriteAsync($"data: {sessionEvent}\n\n", ct);
            await response.Body.FlushAsync(ct);
        });

        // ── Session management ────────────────────────────────────────────

        app.MapGet("/api/books/{slug}/chat/session", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessionId = await agentService.EnsureSessionAsync(slug, ct);
            return Results.Ok(new { sessionId });
        });

        app.MapPost("/api/books/{slug}/chat/session/new", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessionId = await agentService.CreateNewSessionAsync(slug, ct);
            return Results.Ok(new { sessionId });
        });

        app.MapGet("/api/books/{slug}/chat/sessions", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessions = await agentService.ListSessionsAsync(slug, ct);
            return Results.Ok(new { sessions });
        });
    }
}
