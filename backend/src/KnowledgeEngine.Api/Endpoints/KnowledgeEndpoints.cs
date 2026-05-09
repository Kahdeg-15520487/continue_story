using System.Text;
using Microsoft.AspNetCore.Mvc;

namespace KnowledgeEngine.Api.Endpoints;

public static class KnowledgeEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/knowledge");

        // Ensure shared directory exists
        var libraryPath = app.Configuration.GetValue<string>("Library:Path") ?? "/library";
        var sharedDir = Path.Combine(libraryPath, "shared");
        Directory.CreateDirectory(sharedDir);
        Directory.CreateDirectory(Path.Combine(sharedDir, "research"));
        Directory.CreateDirectory(Path.Combine(sharedDir, "worldbuilding"));
        Directory.CreateDirectory(Path.Combine(sharedDir, "references"));

        // List all categories with their entries
        group.MapGet("/", (IConfiguration config) =>
        {
            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var shared = Path.Combine(lib, "shared");
            var categories = new List<object>();

            if (Directory.Exists(shared))
            {
                foreach (var catDir in Directory.GetDirectories(shared).OrderBy(d => d))
                {
                    var catName = Path.GetFileName(catDir);
                    var entries = Directory.GetFiles(catDir, "*.md")
                        .OrderBy(f => f)
                        .Select(f =>
                        {
                            var content = File.ReadAllText(f);
                            var title = content.Split('\n')
                                .FirstOrDefault(l => l.StartsWith("# "))
                                ?.Substring(2).Trim()
                                ?? Path.GetFileNameWithoutExtension(f);
                            return new { file = Path.GetFileName(f), title };
                        })
                        .ToList();
                    categories.Add(new { name = catName, entries });
                }
            }

            return Results.Ok(new { categories });
        });

        // Get a single entry
        group.MapGet("/{category}/{entry}", (string category, string entry, IConfiguration config) =>
        {
            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var filePath = Path.Combine(lib, "shared", category, entry);
            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Entry not found" });

            var content = File.ReadAllText(filePath);
            return Results.Ok(new { file = entry, content });
        });

        // Save/update an entry
        group.MapPut("/{category}/{entry}", async (string category, string entry, [FromBody] SaveKnowledgeEntryRequest req, IConfiguration config) =>
        {
            if (category.Contains("..") || entry.Contains(".."))
                return Results.BadRequest(new { error = "Invalid path" });

            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var catDir = Path.Combine(lib, "shared", category);
            Directory.CreateDirectory(catDir);
            var filePath = Path.Combine(catDir, entry);

            await File.WriteAllTextAsync(filePath, req.Content);
            return Results.Ok(new { saved = true, file = entry });
        });

        // Create a new category
        group.MapPost("/categories", ([FromBody] CreateCategoryRequest req, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Contains("..") || req.Name.Contains("/") || req.Name.Contains("\\"))
                return Results.BadRequest(new { error = "Invalid category name" });

            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var catDir = Path.Combine(lib, "shared", req.Name);
            if (Directory.Exists(catDir))
                return Results.Conflict(new { error = "Category already exists" });

            Directory.CreateDirectory(catDir);
            return Results.Ok(new { created = true, name = req.Name });
        });

        // Delete an entry
        group.MapDelete("/{category}/{entry}", (string category, string entry, IConfiguration config) =>
        {
            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var filePath = Path.Combine(lib, "shared", category, entry);
            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Entry not found" });

            File.Delete(filePath);
            return Results.Ok(new { deleted = true });
        });

        // Delete a category
        group.MapDelete("/{category}", (string category, IConfiguration config) =>
        {
            if (category.Contains(".."))
                return Results.BadRequest(new { error = "Invalid category" });

            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var catDir = Path.Combine(lib, "shared", category);
            if (!Directory.Exists(catDir))
                return Results.NotFound(new { error = "Category not found" });

            Directory.Delete(catDir, recursive: true);
            return Results.Ok(new { deleted = true });
        });
    }
}

public record SaveKnowledgeEntryRequest(string Content);
public record CreateCategoryRequest(string Name);
