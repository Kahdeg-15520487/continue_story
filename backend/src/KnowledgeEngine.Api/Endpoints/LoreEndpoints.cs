using Hangfire;
using KnowledgeEngine.Api.Services;

namespace KnowledgeEngine.Api.Endpoints;

public static class LoreEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/lore");

        // Trigger lore generation (background job)
        group.MapPost("/", (string slug, IBackgroundJobClient jobClient) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var jobId = jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
            return Results.Ok(new { jobId, status = "queued" });
        });

        group.MapGet("/", (string slug, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var wikiDir = Path.Combine(libraryPath, slug, "wiki");

            if (!Directory.Exists(wikiDir))
                return Results.Ok(new { files = Array.Empty<string>() });

            var files = Directory.GetFiles(wikiDir, "*.md")
                .Select(f => Path.GetFileName(f))
                .ToArray();

            return Results.Ok(new { files });
        });

        group.MapGet("/{file}", async (string slug, string file, IConfiguration config) =>
        {
            // Sanitize file name to prevent path traversal
            if (file.Contains("..") || file.Contains('/') || file.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid file name" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var filePath = Path.Combine(libraryPath, slug, "wiki", file);

            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Lore file not found" });

            var content = await File.ReadAllTextAsync(filePath);
            return Results.Ok(new { file, content });
        });
    }
}
