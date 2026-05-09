using Microsoft.AspNetCore.Mvc;

namespace StoryEngine.Api.Endpoints;

public static class KnowledgeEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/knowledge");

        var libraryPath = app.Configuration.GetValue<string>("Library:Path") ?? "/library";
        var sharedDir = Path.Combine(libraryPath, "shared");
        Directory.CreateDirectory(sharedDir);
        Directory.CreateDirectory(Path.Combine(sharedDir, "research"));
        Directory.CreateDirectory(Path.Combine(sharedDir, "worldbuilding"));
        Directory.CreateDirectory(Path.Combine(sharedDir, "references"));

        // List all categories with their entries (parses frontmatter for tags)
        group.MapGet("/", (IConfiguration config) =>
        {
            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var shared = Path.Combine(lib, "shared");
            var categories = new List<object>();
            var allTags = new HashSet<string>();

            if (Directory.Exists(shared))
            {
                foreach (var catDir in Directory.GetDirectories(shared).OrderBy(d => d))
                {
                    var catName = Path.GetFileName(catDir);
                    var entries = Directory.GetFiles(catDir, "*.md")
                        .OrderBy(f => f)
                        .Select(f => ParseEntry(f))
                        .Where(e => e != null)
                        .ToList()!;
                    foreach (var e in entries)
                        foreach (var t in e.Tags)
                            allTags.Add(t);
                    categories.Add(new { name = catName, entries });
                }
            }

            return Results.Ok(new { categories, tags = allTags.OrderBy(t => t).ToList() });
        });

        // Search entries by text or tags
        group.MapGet("/search", ([FromQuery] string? q, [FromQuery] string? tags, IConfiguration config) =>
        {
            var lib = config.GetValue<string>("Library:Path") ?? "/library";
            var shared = Path.Combine(lib, "shared");
            var results = new List<object>();

            var tagFilter = string.IsNullOrWhiteSpace(tags)
                ? Array.Empty<string>()
                : tags.Split(',').Select(t => t.Trim().ToLowerInvariant()).ToArray();

            var textQuery = string.IsNullOrWhiteSpace(q) ? "" : q.Trim().ToLowerInvariant();

            if (!Directory.Exists(shared))
                return Results.Ok(new { results });

            foreach (var catDir in Directory.GetDirectories(shared))
            {
                var catName = Path.GetFileName(catDir);
                foreach (var filePath in Directory.GetFiles(catDir, "*.md"))
                {
                    var entry = ParseEntry(filePath);
                    if (entry == null) continue;

                    // Tag filter
                    if (tagFilter.Length > 0)
                    {
                        var entryTagsLower = entry.Tags.Select(t => t.ToLowerInvariant()).ToHashSet();
                        if (!tagFilter.All(tf => entryTagsLower.Contains(tf)))
                            continue;
                    }

                    // Text search
                    if (!string.IsNullOrEmpty(textQuery))
                    {
                        var searchable = $"{entry.Title} {string.Join(" ", entry.Tags)} {entry.Snippet}".ToLowerInvariant();
                        if (!searchable.Contains(textQuery))
                            continue;
                    }

                    results.Add(new { category = catName, entry.File, entry.Title, entry.Tags });
                }
            }

            return Results.Ok(new { results });
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

    private static EntryInfo? ParseEntry(string filePath)
    {
        var content = File.ReadAllText(filePath);
        var fileName = Path.GetFileName(filePath);
        var title = "";
        var tags = new List<string>();
        var snippet = "";

        // Parse YAML frontmatter
        if (content.StartsWith("---"))
        {
            var end = content.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var frontmatter = content[3..end].Trim();
                foreach (var line in frontmatter.Split('\n'))
                {
                    if (line.StartsWith("title:"))
                        title = line["title:".Length..].Trim().Trim('"', '\'');
                    else if (line.StartsWith("tags:"))
                    {
                        var tagStr = line["tags:".Length..].Trim();
                        if (tagStr.StartsWith('[') && tagStr.EndsWith(']'))
                        {
                            tagStr = tagStr[1..^1];
                            tags = tagStr.Split(',')
                                .Select(t => t.Trim().Trim('\'', '"'))
                                .Where(t => t.Length > 0)
                                .ToList();
                        }
                    }
                }
            }
        }

        // Fallback title from H1
        if (string.IsNullOrEmpty(title))
            title = content.Split('\n')
                .FirstOrDefault(l => l.StartsWith("# "))
                ?["# ".Length..].Trim()
                ?? Path.GetFileNameWithoutExtension(filePath);

        // Snippet: first 200 chars of body (after frontmatter)
        var bodyStart = content.StartsWith("---") ? content.IndexOf("\n---", 3) : 0;
        if (bodyStart > 0) bodyStart = content.IndexOf('\n', bodyStart + 4);
        if (bodyStart < 0) bodyStart = 0;
        var body = content[bodyStart..].Trim();
        snippet = body.Length > 200 ? body[..200] : body;

        return new EntryInfo(fileName, title, tags, snippet);
    }

    private record EntryInfo(string File, string Title, List<string> Tags, string Snippet);
}

public record SaveKnowledgeEntryRequest(string Content);
public record CreateCategoryRequest(string Name);