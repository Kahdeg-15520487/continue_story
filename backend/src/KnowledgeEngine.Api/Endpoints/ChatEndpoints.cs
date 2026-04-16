using Microsoft.AspNetCore.Http;
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
            HttpResponse response,
            CancellationToken ct) =>
        {
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
            var fullPrompt = $"You are answering questions about the book. Here is the book's content and wiki:\n\n{context}\n\n---\n\nUser question: {req.Message}";

            await foreach (var evt in agentService.StreamPromptAsync(fullPrompt, ct))
            {
                await response.WriteAsync($"data: {evt}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        });
    }
}
