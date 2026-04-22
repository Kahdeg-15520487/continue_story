using System.Text;
using System.Text.RegularExpressions;
using Hangfire;
using KnowledgeEngine.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Services;

[Hangfire.AutomaticRetry(Attempts = 0)]
public class ChapterSplitService
{
    private readonly ILogger<ChapterSplitService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public ChapterSplitService(
        ILogger<ChapterSplitService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    public async Task SplitIntoChaptersAsync(string slug)
    {
        using var scope = _scopeFactory.CreateScope();
        var agentService = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("Splitting book into chapters: {Slug}", slug);

        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookMd = Path.Combine(libraryPath, slug, "book.md");
        var chaptersDir = Path.Combine(libraryPath, slug, "chapters");

        var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
        if (book is null)
        {
            _logger.LogError("Book not found in DB: {Slug}", slug);
            return;
        }

        if (!File.Exists(bookMd) || new FileInfo(bookMd).Length == 0)
        {
            _logger.LogError("No book.md to split for {Slug}", slug);
            book.Status = "error";
            book.ErrorMessage = "Cannot split: no book content";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return;
        }

        book.Status = "splitting";
        book.ErrorMessage = null;
        book.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        try
        {
            var bookContent = await File.ReadAllTextAsync(bookMd);

            // Step 1: Take samples from the book for LLM analysis
            var sample = BuildSample(bookContent);
            _logger.LogInformation("Built {SampleLen} char sample for LLM analysis", sample.Length);

            // Step 2: Ask LLM to identify separator patterns
            var separatorPatterns = await DetectSeparatorsAsync(agentService, slug, sample);
            _logger.LogInformation("LLM detected {Count} separator patterns: {Patterns}",
                separatorPatterns.Length, string.Join(" | ", separatorPatterns));

            // Step 3: Split deterministically using detected patterns
            var chapters = SplitByPatterns(bookContent, separatorPatterns);
            _logger.LogInformation("Split produced {Count} chapters", chapters.Count);

            if (chapters.Count == 0)
            {
                // Fallback: single chapter with entire content
                chapters.Add(("ch-001-full-text", bookContent));
                _logger.LogInformation("No separators found, using single chapter fallback");
            }

            // Step 4: Write chapter files
            Directory.CreateDirectory(chaptersDir);
            // Clear existing chapter files (but not scratch files)
            foreach (var existing in Directory.GetFiles(chaptersDir, "ch-*.md"))
            {
                if (!existing.EndsWith(".scratch.md"))
                    File.Delete(existing);
            }

            for (int i = 0; i < chapters.Count; i++)
            {
                var (filename, content) = chapters[i];
                // Derive title from first non-empty line
                var title = content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.TrimStart('#', ' ') ?? $"section-{i + 1}";
                var slugTitle = Slugify(title);
                var numberedName = $"ch-{(i + 1):D3}-{slugTitle}.md";
                var filePath = Path.Combine(chaptersDir, numberedName);

                // Prepend a level-1 heading if the content doesn't start with one
                var fileContent = content.TrimStart();
                if (!fileContent.StartsWith("# "))
                    fileContent = $"# {title}\n\n{fileContent}";

                await File.WriteAllTextAsync(filePath, fileContent);
                _logger.LogInformation("Wrote {File} ({Len} chars)", numberedName, fileContent.Length);
            }

            var finalCount = Directory.GetFiles(chaptersDir, "ch-*.md").Where(f => !f.EndsWith(".scratch.md")).Count();
            _logger.LogInformation("Split {Slug} into {Count} chapters", slug, finalCount);

            // Step 5: Enqueue lore generation
            var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
            _logger.LogInformation("Lore generation enqueued for {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chapter splitting failed for {Slug}", slug);
            book.Status = "error";
            book.ErrorMessage = $"Chapter splitting failed: {ex.Message}";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Build a sample of the book for LLM analysis: beginning, middle, and end.
    /// Caps at ~9KB total to keep the LLM call fast.
    /// </summary>
    private static string BuildSample(string content)
    {
        var totalLen = content.Length;
        const int maxChunk = 3000;

        var parts = new List<string>();

        // Beginning
        parts.Add(content[..Math.Min(maxChunk, totalLen)]);

        // Middle
        if (totalLen > maxChunk * 2)
        {
            var midStart = (totalLen - maxChunk) / 2;
            parts.Add(content[midStart..(midStart + maxChunk)]);
        }

        // End
        if (totalLen > maxChunk)
        {
            var endStart = Math.Max(maxChunk, totalLen - maxChunk);
            parts.Add(content[endStart..]);
        }

        return string.Join("\n\n... [middle section omitted] ...\n\n", parts);
    }

    /// <summary>
    /// Ask the LLM to analyze a book sample and return separator patterns as literal strings.
    /// </summary>
    private async Task<string[]> DetectSeparatorsAsync(IAgentService agentService, string slug, string sample)
    {
        var sessionId = await agentService.EnsureSessionAsync(slug, "read");

        try
        {
            // Build prompt with StringBuilder to avoid raw-string quoting issues
            var sb = new StringBuilder();
            sb.AppendLine("Analyze the following sample from a book and identify ALL separator patterns used to divide chapters or scenes.");
            sb.AppendLine();
            sb.AppendLine("Return ONLY a JSON array of literal separator strings found in the text. For example:");
            sb.AppendLine("- If chapters are separated by blank lines followed by ## Chapter N, return: [\"\\n\\n## \"]");
            sb.AppendLine("- If scenes are separated by a row of asterisks like *** or ＊ ＊ ＊, return: [\"\\n***\\n\"]");
            sb.AppendLine("- If there is a horizontal rule ---, return: [\"\\n---\\n\"]");
            sb.AppendLine("- If chapters use markdown headings like # Chapter N: Title, return: [\"\\n# \"]");
            sb.AppendLine("- If there are no separators at all, return: [\"none\"]");
            sb.AppendLine();
            sb.AppendLine("IMPORTANT RULES:");
            sb.AppendLine("- Return the EXACT literal string as it appears in the text, with surrounding whitespace/newlines");
            sb.AppendLine("- Include ALL separator types found (e.g. both chapter headings AND scene breaks)");
            sb.AppendLine("- Return ONLY the JSON array, nothing else -- no explanation, no markdown, no code fences");
            sb.AppendLine("- Be precise about whitespace");
            sb.AppendLine();
            sb.AppendLine("Book sample:");
            sb.AppendLine();
            sb.Append(sample);

            var prompt = sb.ToString();
            var response = await agentService.SendPromptAsync(sessionId, prompt);

            // Parse the response -- strip markdown code fences if present
            var cleaned = response.Trim();
            if (cleaned.StartsWith("```"))
            {
                var lines = cleaned.Split('\n');
                lines = lines.SkipWhile(l => l.TrimStart().StartsWith("```")).ToArray();
                lines = lines.TakeWhile(l => !l.TrimStart().StartsWith("```")).ToArray();
                cleaned = string.Join('\n', lines).Trim();
            }

            _logger.LogInformation("Separator detection response: {Response}", cleaned);

            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(cleaned);
                if (parsed != null && parsed.Length > 0)
                {
                    var results = parsed.Where(s => s != "none" && !string.IsNullOrWhiteSpace(s)).ToArray();
                    if (results.Length > 0) return results;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse separator JSON: {Response}", cleaned);
            }

            return Array.Empty<string>();
        }
        finally
        {
            try { await agentService.KillSessionAsync(sessionId); } catch { }
        }
    }

    /// <summary>
    /// Split book content using detected separator patterns.
    /// Falls back to paragraph-boundary splitting if no patterns work.
    /// </summary>
    private List<(string filename, string content)> SplitByPatterns(string content, string[] patterns)
    {
        string[]? bestParts = null;
        string? bestPattern = null;

        foreach (var pattern in patterns)
        {
            try
            {
                var parts = content.Split(new[] { pattern }, StringSplitOptions.None);
                var nonEmpty = parts.Where(p => p.Trim().Length > 100).ToArray();

                if (nonEmpty.Length < 2) continue;

                // Sanity: no part should be >80% of total
                if (nonEmpty.Any(p => p.Length > content.Length * 0.8)) continue;

                // Sanity: don't split into 200+ pieces (probably splitting on single newlines)
                if (nonEmpty.Length > 200) continue;

                // Prefer patterns that give reasonable chapter counts (2-50)
                if (bestParts == null || (nonEmpty.Length >= 2 && nonEmpty.Length <= 50))
                {
                    bestParts = nonEmpty;
                    bestPattern = pattern;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pattern '{Pattern}' failed to split content", pattern);
            }
        }

        if (bestParts != null)
        {
            _logger.LogInformation("Using pattern '{Pattern}' which produced {Count} parts", bestPattern, bestParts.Length);
            return bestParts.Select(p => ("", p.Trim())).ToList();
        }

        _logger.LogInformation("No separator pattern worked, falling back to size-based splitting");
        return SplitBySize(content);
    }

    /// <summary>
    /// Split content into chunks of roughly equal size at paragraph boundaries.
    /// Target: ~20KB per chapter.
    /// </summary>
    private List<(string filename, string content)> SplitBySize(string content)
    {
        var targetSize = 20_000;
        var minSize = 5_000;

        var result = new List<(string, string)>();
        var paragraphs = Regex.Split(content, @"\n{2,}");
        var currentChunk = new List<string>();
        var currentLength = 0;

        foreach (var para in paragraphs)
        {
            var paraLen = para.Length + 2;

            if (currentChunk.Count > 0 && currentLength + paraLen > targetSize && currentLength >= minSize)
            {
                var chunk = string.Join("\n\n", currentChunk);
                result.Add(("", chunk));
                currentChunk = new List<string>();
                currentLength = 0;
            }

            currentChunk.Add(para);
            currentLength += paraLen;
        }

        if (currentChunk.Count > 0)
        {
            var chunk = string.Join("\n\n", currentChunk);
            if (result.Count > 0 && chunk.Length < minSize)
            {
                var (fn, prev) = result[^1];
                result[^1] = (fn, prev + "\n\n" + chunk);
            }
            else
            {
                result.Add(("", chunk));
            }
        }

        return result;
    }

    /// <summary>
    /// Simple slugify: lowercase, spaces to hyphens, strip special chars.
    /// </summary>
    private static string Slugify(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "untitled";
        if (title.Length > 60) title = title[..60];

        var slug = title.ToLowerInvariant()
            .Replace(' ', '-')
            .Replace('_', '-');

        slug = Regex.Replace(slug, @"[^a-z0-9\-]", "");
        slug = Regex.Replace(slug, @"-+", "-");
        slug = slug.Trim('-');

        return string.IsNullOrWhiteSpace(slug) ? "untitled" : slug;
    }
}
