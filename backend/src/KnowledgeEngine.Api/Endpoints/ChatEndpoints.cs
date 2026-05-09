using System.Net.Http;
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

            // ── Build lightweight context hint ─────────────────────────────
            // The agent has read/write tools and can fetch content on-demand.
            // We only provide metadata so it knows what exists.
            var storyTitle = book?.Title ?? req.BookSlug;
            var storyAuthor = book?.Author ?? "Unknown";
            var chapterCount = 0;
            var characterCount = 0;
            var locationCount = 0;

            if (Directory.Exists(chaptersDir))
                chapterCount = Directory.GetFiles(chaptersDir, "ch-*.md").Count(f => !f.EndsWith(".scratch.md"));

            if (Directory.Exists(wikiDir))
            {
                try
                {
                    var charsDir = Path.Combine(wikiDir, "characters");
                    if (Directory.Exists(charsDir))
                        characterCount = Directory.GetFiles(charsDir, "*.md").Length;
                    var locsDir = Path.Combine(wikiDir, "locations");
                    if (Directory.Exists(locsDir))
                        locationCount = Directory.GetFiles(locsDir, "*.md").Length;
                }
                catch { }
            }

            var contextHint = new StringBuilder()
                .AppendLine($"[Context: Working on \"{storyTitle}\" by {storyAuthor}.")
                .Append($"{chapterCount} chapters, {characterCount} characters, {locationCount} locations in wiki.");

            if (!string.IsNullOrEmpty(req.ActiveChapterId))
                contextHint.Append($" Active chapter: {req.ActiveChapterId}.");

            contextHint.Append(" Use `ls chapters/`, `read chapters/{id}.md`, `ls wiki/characters/`, `read wiki/summary.md` to explore content as needed.]");
            contextHint.AppendLine();

            // ── Agent session ──────────────────────────────────────────────
            string sessionId;

            if (!string.IsNullOrEmpty(req.SessionId))
            {
                // Verify the session still exists on the agent
                try
                {
                    await agentService.GetSessionInfoAsync(req.SessionId, ct);
                    sessionId = req.SessionId;
                }
                catch
                {
                    // Session gone (agent restarted), create a new one
                    sessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
                }
            }
            else
            {
                sessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
            }

            // No SendPromptAsync here — system prompt is baked into the agent session via appendSystemPrompt.
            // The agent reads chapters/wiki on-demand using its file tools.

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
            // Prepend context hint to the user message so the agent knows what's available
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
                // Check for scratch files for ANY chapter
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
