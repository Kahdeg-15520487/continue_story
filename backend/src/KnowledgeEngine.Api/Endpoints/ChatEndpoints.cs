using Microsoft.AspNetCore.Http;
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
        app.MapPost("/api/chat", async (
            ChatRequest req,
            IAgentService agentService,
            IConfiguration config,
            AppDbContext db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.BookSlug) || req.BookSlug.Contains("..") || req.BookSlug.Contains('/') || req.BookSlug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == req.BookSlug);

            // Load recent conversation history
            List<ChatMessage> chatHistory = [];
            if (book is not null)
            {
                chatHistory = await db.ChatMessages
                    .Where(m => m.BookId == book.Id)
                    .OrderByDescending(m => m.CreatedAt)
                    .Take(20)
                    .OrderBy(m => m.CreatedAt)
                    .ToListAsync();
            }

            var historyText = string.Join("\n\n", chatHistory.Select(m =>
                m.Role == "user" ? $"User: {m.Content}" : $"Assistant: {m.Content}"));

            var response = ctx.Response;
            response.ContentType = "text/event-stream";
            response.Headers.Append("Cache-Control", "no-cache");
            response.Headers.Append("Connection", "keep-alive");

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookMd = Path.Combine(libraryPath, req.BookSlug, "book.md");
            var wikiDir = Path.Combine(libraryPath, req.BookSlug, "wiki");

            var contextParts = new List<string>();

            if (File.Exists(bookMd))
            {
                var content = await File.ReadAllTextAsync(bookMd, ct);
                // Truncate to avoid exceeding context window
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
            var fullPrompt = $"You are answering questions about the book.\n\n" +
                $"# Book Content\n\n{context}\n\n---\n\n" +
                $"# Conversation History\n\n{historyText}\n\n" +
                $"User: {req.Message}";

            var sessionId = await agentService.EnsureSessionAsync(req.BookSlug, "read", ct);
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, fullPrompt, ct))
            {
                await response.WriteAsync($"data: {evt}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            return Results.Ok();
        });
    }
}
