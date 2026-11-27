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
                        using var parsed = JsonDocument.Parse(evt);
                        var root = parsed.RootElement;
                        if (root.TryGetProperty("kind", out var kind) && kind.GetString() == "value"
                            && root.TryGetProperty("value", out var value))
                        {
                            var text = NewestAssistantText(value);
                            if (!string.IsNullOrEmpty(text)) assistantText = text;
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

        // The model this book's conversation is currently using.
        app.MapGet("/api/books/{slug}/chat/model", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessionId = await agentService.EnsureSessionAsync(slug, ct);
            var result = await agentService.GetConversationAsync(sessionId, ct);
            if (result.StatusCode != 200)
                return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);

            using var doc = System.Text.Json.JsonDocument.Parse(result.Body);
            if (!doc.RootElement.TryGetProperty("agent", out var agent) ||
                !agent.TryGetProperty("model", out var model))
                return Results.NotFound(new { error = "Conversation model unavailable" });

            var provider = model.TryGetProperty("provider", out var providerEl) ? providerEl.GetString() : null;
            var modelId = model.TryGetProperty("modelId", out var modelEl) ? modelEl.GetString() : null;
            if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(modelId))
                return Results.NotFound(new { error = "Conversation model unavailable" });

            return Results.Ok(new { model = new { provider, modelId } });
        });

        // Pick the model for this book's conversation (per-chat model selector).
        app.MapPost("/api/books/{slug}/chat/model", async (
            string slug,
            HttpContext ctx,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var sessionId = await agentService.EnsureSessionAsync(slug, ct);
            var result = await agentService.ConfigureConversationAsync(sessionId, body, ct);
            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
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

        // Durable transcript page (used by ConversationTranscript on the client).
        app.MapGet("/api/books/{slug}/chat/entries", async (
            string slug,
            int? limit,
            long? after,
            long? before,
            string? order,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessionId = await agentService.EnsureSessionAsync(slug, ct);
            var json = await agentService.GetEntriesAsync(sessionId, limit ?? 50, after, before, order, ct);
            return Results.Content(json, "application/json");
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

    private static string NewestAssistantText(JsonElement value)
    {
        if (!value.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return "";

        var text = "";
        foreach (var entry in entries.EnumerateArray())
        {
            if (!entry.TryGetProperty("kind", out var kind) || kind.GetString() != "pi.assistant") continue;
            if (!entry.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Array) continue;
            var first = model.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("content", out var content))
            {
                var extracted = ExtractText(content);
                if (!string.IsNullOrEmpty(extracted)) text = extracted;
            }
        }
        return text;
    }

    private static string ExtractText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? "";
        if (element.ValueKind != JsonValueKind.Array) return "";

        var sb = new StringBuilder();
        foreach (var block in element.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                sb.Append(block.GetString());
                continue;
            }
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }
}
