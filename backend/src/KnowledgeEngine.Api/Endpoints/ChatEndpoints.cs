using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;

namespace KnowledgeEngine.Api.Endpoints;

public static class ChatEndpoints
{
    public static void Map(WebApplication app)
    {
        // SSE streaming chat endpoint
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
            var bookMd = Path.Combine(libraryPath, req.BookSlug, "book.md");
            var wikiDir = Path.Combine(libraryPath, req.BookSlug, "wiki");
            var chaptersDir = Path.Combine(libraryPath, req.BookSlug, "chapters");

            // Build context from chapters + wiki files
            var contextParts = new List<string>();

            // Chapter list for navigation context
            if (Directory.Exists(chaptersDir))
            {
                var chapterFiles = Directory.GetFiles(chaptersDir, "ch-*.md").OrderBy(f => f).ToList();
                if (chapterFiles.Count > 0)
                {
                    var chapterList = new System.Text.StringBuilder("# Chapters\n\n");
                    for (int i = 0; i < chapterFiles.Count; i++)
                    {
                        var chContent = await File.ReadAllTextAsync(chapterFiles[i], ct);
                        var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? $"Chapter {i + 1}";
                        chapterList.AppendLine($"{i + 1}. {title} ({Path.GetFileName(chapterFiles[i])})");
                    }
                    contextParts.Add(chapterList.ToString());

                    // If user mentions a specific chapter, include its content
                    if (!string.IsNullOrEmpty(req.Message))
                    {
                        var msgLower = req.Message.ToLowerInvariant();
                        for (int i = 0; i < chapterFiles.Count; i++)
                        {
                            var chContent = await File.ReadAllTextAsync(chapterFiles[i], ct);
                            var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? $"Chapter {i + 1}";
                            if (msgLower.Contains($"chapter {i + 1}") || msgLower.Contains(title.ToLowerInvariant()))
                            {
                                var truncated = chContent.Length > 15_000 ? chContent[..15_000] + "\n[... truncated ...]" : chContent;
                                contextParts.Add($"# Active Chapter: {title}\n\n{truncated}");
                                break;
                            }
                        }
                    }
                }
            }
            else if (File.Exists(bookMd))
            {
                // No chapters — use full book.md
                var content = await File.ReadAllTextAsync(bookMd, ct);
                if (content.Length > 50_000)
                    content = content[..50_000] + "\n\n[... truncated ...]";
                contextParts.Add($"# Book Content\n\n{content}");
            }

            var wikiFiles = Directory.Exists(wikiDir)
                ? Directory.GetFiles(wikiDir, "*.md")
                : Array.Empty<string>();

            foreach (var wikiFile in wikiFiles)
            {
                var wikiContent = await File.ReadAllTextAsync(wikiFile, ct);
                if (wikiContent.Length > 10_000)
                    wikiContent = wikiContent[..10_000] + "\n\n[... truncated ...]";
                contextParts.Add($"# {Path.GetFileName(wikiFile)}\n\n{wikiContent}");
            }

            var context = string.Join("\n\n---\n\n", contextParts);

            // Ensure session exists (will restore from persistent storage if available)
            var sessionId = await agentService.EnsureSessionAsync(req.BookSlug, "read", ct);

            // For fresh sessions (no messages yet), inject book context as the first message
            // so the agent knows what book it's working with. On restored sessions, skip this
            // — the agent already has the context from previous conversation.
            try
            {
                var info = await agentService.GetSessionInfoAsync(sessionId, ct);
                if (info.MessageCount == 0)
                {
                    var contextPrompt = $"You are answering questions about the book.\n\n{context}";
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
            }
            catch
            {
                // If info fails (agent restart, etc), send context anyway to be safe
                try
                {
                    var contextPrompt = $"You are answering questions about the book.\n\n{context}";
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
                catch { }
            }

            // Send just the user message — history is managed by the Pi SDK session
            var fullPrompt = $"User: {req.Message}";

            // Save user message to DB for UI history display
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

            var assistantText = "";
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, fullPrompt, ct))
            {
                await response.WriteAsync($"data: {evt}\n\n", ct);
                await response.Body.FlushAsync(ct);

                // Capture assistant text for DB storage
                try
                {
                    var parsed = System.Text.Json.JsonDocument.Parse(evt);
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

            // Save assistant response to DB
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
        });
    }
}
