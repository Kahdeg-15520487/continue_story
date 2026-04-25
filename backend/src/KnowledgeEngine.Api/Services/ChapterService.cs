using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;
using System.Text.RegularExpressions;

namespace KnowledgeEngine.Api.Services;

public class ChapterService
{
    private readonly ILogger<ChapterService> _logger;
    private readonly IConfiguration _config;

    public ChapterService(ILogger<ChapterService> logger, IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    private string GetChaptersDir(string slug)
    {
        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        return Path.Combine(libraryPath, slug, "chapters");
    }

    private static string Slugify(string title)
    {
        var parts = title
            .ToLowerInvariant()
            .Split(' ', '_', '/', '\\')
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Regex.Replace(s, "[^a-z0-9]", ""))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray();
        return parts.Length > 0 ? string.Join('-', parts) : "untitled";
    }

    private static string GetNextNumber(string chaptersDir)
    {
        if (!Directory.Exists(chaptersDir)) return "001";
        var existing = Directory.GetFiles(chaptersDir, "ch-*.md")
            .Where(f => !f.EndsWith(".scratch.md"))
            .Select(f => Path.GetFileName(f))
            .Where(f => f.StartsWith("ch-") && f.EndsWith(".md"))
            .Select(f => f.Substring(3, 3)) // extract "001" from "ch-001-..."
            .Where(s => int.TryParse(s, out _))
            .Select(int.Parse)
            .DefaultIfEmpty(0)
            .Max();
        return (existing + 1).ToString("000");
    }

    private static string ExtractTitleFromContent(string content)
    {
        var firstLine = content.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
        var match = Regex.Match(firstLine, @"^#+\s+(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : "Untitled";
    }

    public Task<List<ChapterInfo>> ListChaptersAsync(string slug)
    {
        var dir = GetChaptersDir(slug);
        if (!Directory.Exists(dir)) return Task.FromResult(new List<ChapterInfo>());

        var chapters = Directory.GetFiles(dir, "*.md")
            .Where(f => !f.EndsWith(".scratch.md"))
            .OrderBy(f => f)
            .Select((f, i) =>
            {
                var content = File.ReadAllText(f);
                var title = ExtractTitleFromContent(content);
                var wordCount = content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
                return new ChapterInfo(
                    Id: Path.GetFileNameWithoutExtension(f),
                    Number: i + 1,
                    Title: title,
                    WordCount: wordCount,
                    FileName: Path.GetFileName(f)
                );
            })
            .ToList();

        return Task.FromResult(chapters);
    }

    public Task<ChapterContent?> GetChapterAsync(string slug, string id)
    {
        var dir = GetChaptersDir(slug);
        var file = Path.Combine(dir, $"{id}.md");
        if (!File.Exists(file)) return Task.FromResult<ChapterContent?>(null);

        var content = File.ReadAllText(file);
        var title = ExtractTitleFromContent(content);
        return Task.FromResult<ChapterContent?>(new ChapterContent(id, title, content));
    }

    public async Task SaveChapterAsync(string slug, string id, string content)
    {
        var dir = GetChaptersDir(slug);
        var file = Path.Combine(dir, $"{id}.md");
        if (!File.Exists(file))
            throw new FileNotFoundException($"Chapter not found: {id}");
        await File.WriteAllTextAsync(file, content);
    }

    public async Task<ChapterInfo> InsertChapterAsync(string slug, string title, string? afterChapterId)
    {
        var dir = GetChaptersDir(slug);
        Directory.CreateDirectory(dir);

        // Get existing chapters
        var existing = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "ch-*.md").Where(f => !f.EndsWith(".scratch.md")).OrderBy(f => f).ToList()
            : new List<string>();

        // Determine insert position
        int insertIndex;
        if (afterChapterId != null)
        {
            var afterFile = existing.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == afterChapterId);
            insertIndex = afterFile != null ? existing.IndexOf(afterFile) + 1 : existing.Count;
        }
        else
        {
            insertIndex = existing.Count;
        }

        // Create temp file with new chapter
        var slugified = Slugify(title);
        var newContent = $"# {title}\n\n";
        var tempFile = Path.Combine(dir, $"_new-{slugified}.md");
        await File.WriteAllTextAsync(tempFile, newContent);

        // Rebuild chapter list with correct numbering
        var allFiles = new List<string>(existing);
        allFiles.Insert(insertIndex, tempFile);

        // Rename all files with new sequence numbers
        for (int i = 0; i < allFiles.Count; i++)
        {
            var srcName = Path.GetFileName(allFiles[i]);
            var srcTitle = srcName.StartsWith("_new-")
                ? slugified
                : srcName.Substring(7, srcName.Length - 10); // strip "ch-NNN-" and ".md"
            var newName = $"ch-{(i + 1):000}-{srcTitle}.md";

            if (srcName != newName)
            {
                File.Move(allFiles[i], Path.Combine(dir, newName), overwrite: true);
            }
        }

        // Clean up temp file if it wasn't renamed
        if (File.Exists(tempFile)) File.Delete(tempFile);

        var finalName = $"ch-{(insertIndex + 1):000}-{slugified}.md";
        var content = await File.ReadAllTextAsync(Path.Combine(dir, finalName));
        var wordCount = content.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;

        return new ChapterInfo(
            Id: Path.GetFileNameWithoutExtension(finalName),
            Number: insertIndex + 1,
            Title: title,
            WordCount: wordCount,
            FileName: finalName
        );
    }

    public Task DeleteChapterAsync(string slug, string id)
    {
        var dir = GetChaptersDir(slug);
        var file = Path.Combine(dir, $"{id}.md");
        if (!File.Exists(file)) throw new FileNotFoundException($"Chapter not found: {id}");

        File.Delete(file);

        // Renumber remaining chapters
        var remaining = Directory.GetFiles(dir, "ch-*.md")
            .Where(f => !f.EndsWith(".scratch.md"))
            .OrderBy(f => f).ToList();
        for (int i = 0; i < remaining.Count; i++)
        {
            var oldName = Path.GetFileName(remaining[i]);
            var titlePart = oldName.Substring(7, oldName.Length - 10); // strip "ch-NNN-" and ".md"
            var newName = $"ch-{(i + 1):000}-{titlePart}.md";
            if (oldName != newName)
            {
                File.Move(remaining[i], Path.Combine(dir, newName), overwrite: true);
            }
        }

        return Task.CompletedTask;
    }

    public Task ReorderChaptersAsync(string slug, string[] orderedIds)
    {
        var dir = GetChaptersDir(slug);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"No chapters for: {slug}");

        // Read all existing files, map id -> content + title
        var chapters = new Dictionary<string, (string title, string content)>();
        foreach (var file in Directory.GetFiles(dir, "ch-*.md").Where(x => !x.EndsWith(".scratch.md")))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            var content = File.ReadAllText(file);
            var title = file.Substring(file.LastIndexOf("ch-") + 7); // strip "ch-NNN-"
            title = title.Substring(0, title.Length - 3); // strip ".md"
            chapters[id] = (title, content);
        }

        // Validate all ids exist
        foreach (var id in orderedIds)
        {
            if (!chapters.ContainsKey(id))
                throw new FileNotFoundException($"Chapter not found: {id}");
        }

        // Delete all existing files
        foreach (var f in Directory.GetFiles(dir, "ch-*.md").Where(x => !x.EndsWith(".scratch.md")))
            File.Delete(f);

        // Re-create in new order
        for (int i = 0; i < orderedIds.Length; i++)
        {
            var (title, content) = chapters[orderedIds[i]];
            var newName = $"ch-{(i + 1):000}-{title}.md";
            File.WriteAllText(Path.Combine(dir, newName), content);
        }

        return Task.CompletedTask;
    }
}

public record ChapterInfo(string Id, int Number, string Title, int WordCount, string FileName);
public record ChapterContent(string Id, string Title, string Content);
