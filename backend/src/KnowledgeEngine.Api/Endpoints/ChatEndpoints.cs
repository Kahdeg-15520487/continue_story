using System.Text;
using System.Text.Json;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class ChatEndpoints
{
    /// <summary>
    /// Keywords that indicate the user wants to modify story content (not just ask questions).
    /// </summary>
    private static readonly string[] EditIntentKeywords = new[]
    {
        "rewrite", "modify", "change", "edit", "revise", "rephrase",
        "rewrite this chapter", "change the", "make it", "update the",
        "add a scene", "remove", "delete this", "replace", "expand",
        "shorten", "condense", "fix this", "improve this", "reword"
    };

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
            var chapterFiles = Array.Empty<string>();
            string? activeChapterTitle = null;
            string? activeChapterContent = null;

            if (Directory.Exists(chaptersDir))
            {
                chapterFiles = Directory.GetFiles(chaptersDir, "ch-*.md")
                    .Where(f => !f.EndsWith(".scratch.md"))
                    .OrderBy(f => f)
                    .ToArray();

                if (chapterFiles.Length > 0)
                {
                    // Chapter TOC
                    var chapterList = new StringBuilder("# Chapters\n\n");
                    for (int i = 0; i < chapterFiles.Length; i++)
                    {
                        var chContent = await File.ReadAllTextAsync(chapterFiles[i], ct);
                        var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? $"Chapter {i + 1}";
                        chapterList.AppendLine($"{i + 1}. {title} ({Path.GetFileName(chapterFiles[i])})");
                    }
                    contextParts.Add(chapterList.ToString());
                }
            }

            // Active chapter content (always include if available)
            if (!string.IsNullOrEmpty(req.ActiveChapterId) && Directory.Exists(chaptersDir))
            {
                var activeFile = Path.Combine(chaptersDir, $"{req.ActiveChapterId}.md");
                if (File.Exists(activeFile))
                {
                    activeChapterContent = await File.ReadAllTextAsync(activeFile, ct);
                    activeChapterTitle = activeChapterContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? req.ActiveChapterId;
                    var truncated = activeChapterContent.Length > 30_000
                        ? activeChapterContent[..30_000] + "\n\n[... truncated ...]"
                        : activeChapterContent;
                    contextParts.Add($"# Current Chapter: {activeChapterTitle}\n\n{truncated}");
                }
            }

            // If user mentions another chapter by name/number, include it too
            if (!string.IsNullOrEmpty(req.Message) && chapterFiles.Length > 0)
            {
                var msgLower = req.Message.ToLowerInvariant();
                foreach (var chFile in chapterFiles)
                {
                    var chContent = await File.ReadAllTextAsync(chFile, ct);
                    var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "";
                    var fileName = Path.GetFileNameWithoutExtension(chFile);

                    // Skip the active chapter (already included)
                    if (fileName == req.ActiveChapterId) continue;

                    var match = false;
                    if (!string.IsNullOrEmpty(title) && msgLower.Contains(title.ToLowerInvariant()))
                        match = true;
                    // Match "chapter N"
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

            // Wiki files (always include — they're small)
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

            // ── Detect edit intent ─────────────────────────────────────────
            var hasEditIntent = DetectEditIntent(req.Message ?? "");

            // ── Agent session ──────────────────────────────────────────────
            var mode = hasEditIntent ? "write" : "read";
            var sessionId = await agentService.EnsureSessionAsync(req.BookSlug, mode, ct);

            // Inject context on fresh sessions
            try
            {
                var info = await agentService.GetSessionInfoAsync(sessionId, ct);
                if (info.MessageCount == 0)
                {
                    var contextPrompt = hasEditIntent
                        ? $"You are helping edit a book. You have access to the full book context.\n\n{context}\n\nWhen the user asks you to modify or rewrite content, write the COMPLETE modified chapter to the scratch file path they specify. Keep everything else the same unless instructed otherwise."
                        : $"You are answering questions about the book. You have access to the full book context.\n\n{context}";
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
            }
            catch
            {
                // Session info failed — inject context anyway
                try
                {
                    var contextPrompt = $"You are helping with a book.\n\n{context}";
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
                catch { }
            }

            // ── Build user prompt ──────────────────────────────────────────
            string userPrompt;
            string? scratchChapterId = null;

            if (hasEditIntent && !string.IsNullOrEmpty(req.ActiveChapterId))
            {
                // Instruct agent to write scratch file
                scratchChapterId = req.ActiveChapterId;
                var scratchPath = $"chapters/{req.ActiveChapterId}.scratch.md";
                var sb = new StringBuilder();
                sb.AppendLine($"User request: {req.Message}");
                sb.AppendLine();
                sb.AppendLine($"IMPORTANT: Write the COMPLETE modified chapter to: {scratchPath}");
                sb.AppendLine("Include the full chapter content with the requested changes applied. Do not skip or summarize any parts.");
                userPrompt = sb.ToString();
            }
            else if (hasEditIntent && string.IsNullOrEmpty(req.ActiveChapterId))
            {
                // Edit intent but no chapter selected — just answer, no file write
                userPrompt = $"User: {req.Message}\n\nNote: No chapter is currently active. Tell the user to select a chapter first if they want to edit.";
            }
            else
            {
                userPrompt = $"User: {req.Message}";
            }

            // ── Save user message ──────────────────────────────────────────
            if (book is not null)
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    BookId = book.Id,
                    Role = "user",
                    Content = req.Message,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // ── Stream response ────────────────────────────────────────────
            var assistantText = "";
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, userPrompt, ct))
            {
                await response.WriteAsync($"data: {evt}\n\n", ct);
                await response.Body.FlushAsync(ct);

                // Capture assistant text for DB storage
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

            // ── Edit done event ────────────────────────────────────────────
            if (hasEditIntent && scratchChapterId != null)
            {
                var scratchFile = Path.Combine(libraryPath, req.BookSlug, "chapters", $"{scratchChapterId}.scratch.md");
                var scratchExists = File.Exists(scratchFile);

                if (scratchExists)
                {
                    var doneEvent = JsonSerializer.Serialize(new
                    {
                        type = "edit_done",
                        chapterId = scratchChapterId,
                        scratchPath = $"chapters/{scratchChapterId}.scratch.md",
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
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // Kill write sessions (read sessions persist for conversation continuity)
            if (hasEditIntent)
            {
                try { await agentService.KillSessionAsync(sessionId, ct); } catch { }
            }
        });
    }

    /// <summary>
    /// Simple keyword-based edit intent detection.
    /// </summary>
    private static bool DetectEditIntent(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var lower = message.ToLowerInvariant();
        return EditIntentKeywords.Any(kw => lower.Contains(kw));
    }
}
