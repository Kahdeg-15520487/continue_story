using System.Text.Json;
using KnowledgeEngine.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class EditorEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/content");

        // Get book markdown content
        group.MapGet("/", async (string slug, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookMd = Path.Combine(libraryPath, slug, "book.md");

            if (!File.Exists(bookMd))
                return Results.NotFound(new { error = "Book content not found. Has it been converted?" });

            var content = await File.ReadAllTextAsync(bookMd);
            return Results.Ok(new { slug, content });
        });

        // Save book markdown content
        group.MapPut("/", async (string slug, UpdateContentRequest req, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookMd = Path.Combine(libraryPath, slug, "book.md");

            if (!File.Exists(bookMd))
                return Results.NotFound(new { error = "Book content not found" });

            await File.WriteAllTextAsync(bookMd, req.Content);
            return Results.Ok(new { slug, saved = true });
        });

        // Get book metadata JSON
        group.MapGet("/metadata", async (string slug, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var metaPath = Path.Combine(libraryPath, slug, "metadata.json");

            if (!File.Exists(metaPath))
                return Results.NotFound(new { error = "Metadata not found" });

            var json = await File.ReadAllTextAsync(metaPath);
            var metadata = JsonDocument.Parse(json);
            return Results.Ok(metadata.RootElement);
        });
    }
}

public record UpdateContentRequest(string Content);
