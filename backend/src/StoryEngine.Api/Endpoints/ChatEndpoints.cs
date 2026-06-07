using System.Net.Http;
using System.Text;
using System.Text.Json;
using StoryEngine.Api.Data;
using StoryEngine.Api.Models;
using StoryEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace StoryEngine.Api.Endpoints;

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

            // ── Build lightweight context hint ─────────────────────────────
            // Agent has read/write tools — only provide metadata so it knows what exists.
            var storyTitle = book?.Title ?? req.BookSlug;
            var storyAuthor = book?.Author ?? "Unknown";

            // ── Build rich context: chapter titles, characters, locations, summary ──
            var chapterTitles = new List<string>();
            var characterNames = new List<string>();
            var locationNames = new List<string>();
            var plotSummary = "";

            if (Directory.Exists(chaptersDir))
            {
                foreach (var f in Directory.GetFiles(chaptersDir, "ch-*.md").Where(f => !f.EndsWith(".scratch.md")).OrderBy(f => f))
                {
                    try
                    {
                        var firstLine = File.ReadLines(f).FirstOrDefault()?.TrimStart('#', ' ')?.Trim();
                        if (!string.IsNullOrEmpty(firstLine) && firstLine.Length < 120)
                            chapterTitles.Add(firstLine);
                    }
                    catch { }
                }
            }

            if (Directory.Exists(wikiDir))
            {
                try
                {
                    var charsDir = Path.Combine(wikiDir, "characters");
                    if (Directory.Exists(charsDir))
                        characterNames = Directory.GetFiles(charsDir, "*.md")
                            .Select(f => Path.GetFileNameWithoutExtension(f))
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .ToList();

                    var locsDir = Path.Combine(wikiDir, "locations");
                    if (Directory.Exists(locsDir))
                        locationNames = Directory.GetFiles(locsDir, "*.md")
                            .Select(f => Path.GetFileNameWithoutExtension(f))
                            .Where(n => !string.IsNullOrWhiteSpace(n))
                            .ToList();

                    var summaryFile = Path.Combine(wikiDir, "summary.md");
                    if (File.Exists(summaryFile))
                    {
                        var summaryText = await File.ReadAllTextAsync(summaryFile, ct);
                        // Strip markdown headings and take first 500 chars
                        var stripped = string.Join(" ", summaryText.Split('\n')
                            .Where(l => !l.TrimStart().StartsWith("#"))
                            .Select(l => l.Trim())
                            .Where(l => l.Length > 0));
                        plotSummary = stripped.Length > 500 ? stripped[..500] + "..." : stripped;
                    }
                }
                catch { }
            }

            var contextHint = new StringBuilder()
                .AppendLine($"[Context: \"{storyTitle}\" by {storyAuthor}.");

            if (chapterTitles.Count > 0)
            {
                contextHint.Append("Chapters: ");
                for (int i = 0; i < chapterTitles.Count; i++)
                {
                    if (i > 0) contextHint.Append(" | ");
                    contextHint.Append($"[{i + 1}] {chapterTitles[i]}");
                }
                contextHint.AppendLine();
            }

            if (characterNames.Count > 0)
                contextHint.AppendLine($"Characters: {string.Join(", ", characterNames)}.");

            if (locationNames.Count > 0)
                contextHint.AppendLine($"Locations: {string.Join(", ", locationNames)}.");

            if (!string.IsNullOrEmpty(plotSummary))
                contextHint.AppendLine($"Plot: {plotSummary}");

            if (!string.IsNullOrEmpty(req.ActiveChapterId))
                contextHint.AppendLine($"Active chapter: {req.ActiveChapterId}.");

            contextHint.AppendLine("Use `read chapters/{id}.md` for full text, `read wiki/characters/{name}.md` for character details, `read wiki/summary.md` for full plot.]");
            contextHint.AppendLine();

            // ── Agent session ──────────────────────────────────────────────
            string sessionId;

            if (!string.IsNullOrEmpty(req.SessionId))
            {
                try
                {
                    await agentService.GetSessionInfoAsync(req.SessionId, ct);
                    sessionId = req.SessionId;
                }
                catch
                {
                    sessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
                }
            }
            else
            {
                sessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
            }

            // System prompt is baked into the agent session; agent reads chapters/wiki on-demand.

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
            var messageWithHint = contextHint.ToString() + req.Message;
            var assistantText = "";
            var streamSessionId = sessionId;

        streamRetry:
            try
            {
                await foreach (var evt in agentService.StreamPromptAsync(streamSessionId, messageWithHint, ct))
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
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                // Session was lost (e.g. after abort/stop). Create a fresh one and retry.
                streamSessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
                sessionId = streamSessionId;
                goto streamRetry;
            }

            // ── Check for scratch files or direct edits ────────────────────
            if (Directory.Exists(chaptersDir))
            {
                foreach (var scratchFile in Directory.GetFiles(chaptersDir, "*.scratch.md"))
                {
                    var chapterId = Path.GetFileName(scratchFile).Replace(".scratch.md", "");
                    var doneEvent = JsonSerializer.Serialize(new
                    {
                        type = "edit_done",
                        chapterId,
                        scratchPath = $"chapters/{Path.GetFileName(scratchFile)}",
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

        // Abort the current agent response
        app.MapPost("/api/books/{slug}/chat/abort", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            var sessions = await agentService.ListSessionsAsync(slug, ct);
            var active = sessions.FirstOrDefault();
            if (active is null)
                return Results.NotFound(new { error = "No active session" });

            var lastMsg = await agentService.AbortSessionAsync(active.Id, ct);
            return Results.Ok(new { aborted = true, lastUserMessage = lastMsg });
        });

        // Retry: get last user message from session history
        app.MapGet("/api/books/{slug}/chat/last-message", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            var lastMsg = await agentService.GetLastUserMessageAsync(slug, ct);
            return lastMsg is not null
                ? Results.Ok(new { lastUserMessage = lastMsg })
                : Results.NotFound(new { error = "No previous message" });
        });
    }
}
