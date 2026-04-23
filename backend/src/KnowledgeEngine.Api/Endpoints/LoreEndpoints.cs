using Hangfire;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class LoreEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/lore");

        // Trigger lore generation (background job)
        group.MapPost("/", (string slug, IBackgroundJobClient jobClient) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            var jobId = jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
            return Results.Ok(new { jobId, status = "queued" });
        });

        // Retry lore generation
        group.MapPost("/retry", async (string slug, IBackgroundJobClient jobClient, Data.AppDbContext db) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null) return Results.NotFound(new { error = "Book not found" });

            if (book.Status != "error" && book.Status != "generating-lore")
                return Results.BadRequest(new { error = $"Book is in '{book.Status}' status, not retry-able" });

            book.Status = "generating-lore";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var jobId = jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
            return Results.Ok(new { jobId, status = "re-queued" });
        });

        // List wiki entities: returns categories with their entity files
        // GET /api/books/{slug}/lore
        // Returns: { categories: [{ name: "characters", entities: [{ id, name, file }] }, ...], hasSummary: bool }
        group.MapGet("/", (string slug, IConfiguration config) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var wikiDir = Path.Combine(libraryPath, slug, "wiki");

            if (!Directory.Exists(wikiDir))
                return Results.Ok(new { categories = Array.Empty<object>(), hasSummary = false });

            var categories = new List<object>();
            var catLabels = new Dictionary<string, string>
            {
                ["characters"] = "Characters",
                ["locations"] = "Locations",
            };

            foreach (var cat in new[] { "characters", "locations" })
            {
                var catDir = Path.Combine(wikiDir, cat);
                if (!Directory.Exists(catDir)) continue;

                var entities = new List<object>();
                foreach (var file in Directory.GetFiles(catDir, "*.md").OrderBy(f => f))
                {
                    var content = File.ReadAllText(file);
                    // Extract name from first # heading
                    var name = content.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2).Trim()
                        ?? Path.GetFileNameWithoutExtension(file);
                    entities.Add(new
                    {
                        id = Path.GetFileNameWithoutExtension(file),
                        name,
                        file = $"{cat}/{Path.GetFileName(file)}",
                    });
                }

                categories.Add(new
                {
                    name = cat,
                    label = catLabels.GetValueOrDefault(cat, cat),
                    entities,
                });
            }

            var hasSummary = File.Exists(Path.Combine(wikiDir, "summary.md"));

            return Results.Ok(new { categories, hasSummary });
        });

        // Read a wiki entity file
        // GET /api/books/{slug}/lore/{category}/{entity}
        group.MapGet("/{category}/{entity}", async (string slug, string category, string entity, IConfiguration config) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            if (category.Contains("..") || category.Contains('/') || category.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid category" });
            if (entity.Contains("..") || entity.Contains('/') || entity.Contains('\\') || !entity.EndsWith(".md"))
                return Results.BadRequest(new { error = "Invalid entity" });

            // Only allow known categories
            if (category != "characters" && category != "locations" && category != "root")
                return Results.BadRequest(new { error = "Unknown category" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var filePath = category == "root"
                ? Path.Combine(libraryPath, slug, "wiki", entity)
                : Path.Combine(libraryPath, slug, "wiki", category, entity);

            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Entity not found" });

            var content = await File.ReadAllTextAsync(filePath);
            return Results.Ok(new { file = $"{category}/{entity}", content });
        });

        // Read summary
        group.MapGet("/summary", async (string slug, IConfiguration config) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var filePath = Path.Combine(libraryPath, slug, "wiki", "summary.md");

            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Summary not found" });

            var content = await File.ReadAllTextAsync(filePath);
            return Results.Ok(new { file = "summary.md", content });
        });
    }

    private static bool InvalidSlug(string slug) =>
        string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\');
}
