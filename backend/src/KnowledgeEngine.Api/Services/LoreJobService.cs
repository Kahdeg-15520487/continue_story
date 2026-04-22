using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;

namespace KnowledgeEngine.Api.Services;

[Hangfire.AutomaticRetry(Attempts = 0)]
public class LoreJobService
{
    private readonly ILogger<LoreJobService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public LoreJobService(
        ILogger<LoreJobService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    public async Task GenerateLoreAsync(string slug)
    {
        using var scope = _scopeFactory.CreateScope();
        var agentService = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("Generating lore for book: Slug={Slug}", slug);

        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookMd = Path.Combine(libraryPath, slug, "book.md");
        var wikiDir = Path.Combine(libraryPath, slug, "wiki");

        var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
        if (book is null)
        {
            _logger.LogError("Book not found in DB: {Slug}", slug);
            return;
        }

        // Guard: already done (Hangfire retry after success)
        if (book.Status == "lore-ready" && Directory.Exists(wikiDir)
            && Directory.GetFiles(wikiDir, "*.md").Length > 0)
        {
            _logger.LogInformation("Lore already generated for {Slug}, skipping", slug);
            return;
        }

        if (!File.Exists(bookMd) || new FileInfo(bookMd).Length == 0)
        {
            book.Status = "error";
            book.ErrorMessage = "Cannot generate lore: book has no content";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogError("Lore generation skipped: no book.md for {Slug}", slug);
            return;
        }

        Directory.CreateDirectory(wikiDir);

        var expectedFiles = new[] { "characters.md", "locations.md", "themes.md", "summary.md", "chapter-summaries.md" };

        // Load existing checkpoints
        var checkpoints = await db.LoreCheckpoints
            .Where(c => c.BookId == book.Id)
            .ToDictionaryAsync(c => c.TargetFile);

        // Determine which files still need to be generated
        var filesToGenerate = expectedFiles
            .Where(f => !File.Exists(Path.Combine(wikiDir, f))
                || checkpoints.GetValueOrDefault(f)?.Status != "done")
            .ToList();

        if (filesToGenerate.Count == 0)
        {
            book.Status = "lore-ready";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogInformation("All lore files already done for {Slug}", slug);
            return;
        }

        book.Status = "generating-lore";
        book.ErrorMessage = null;
        book.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Create checkpoints for files we'll generate
        foreach (var file in filesToGenerate)
        {
            if (!checkpoints.ContainsKey(file))
            {
                db.LoreCheckpoints.Add(new LoreCheckpoint
                {
                    BookId = book.Id,
                    Slug = slug,
                    TargetFile = file,
                    Status = "pending",
                    UpdatedAt = DateTime.UtcNow,
                });
            }
            else
            {
                // Reset failed/pending checkpoints for retry
                checkpoints[file].Status = "pending";
                checkpoints[file].UpdatedAt = DateTime.UtcNow;
            }
        }
        await db.SaveChangesAsync();

        // Reload checkpoints after saving new ones
        checkpoints = await db.LoreCheckpoints
            .Where(c => c.BookId == book.Id)
            .ToDictionaryAsync(c => c.TargetFile);

        // Build prompt listing only missing files
        var fileList = string.Join(", ", filesToGenerate);
        var chaptersDir = Path.Combine(libraryPath, slug, "chapters");
        var hasChapters = Directory.Exists(chaptersDir) && Directory.GetFiles(chaptersDir, "ch-*.md").Length > 0;

        var prompt = $"Read the book at book.md and extract lore using the lore-extraction skill. " +
            $"Generate these wiki files in the wiki/ directory: {fileList}. " +
            $"Follow the skill's output format exactly.";

        if (hasChapters && filesToGenerate.Contains("chapter-summaries.md"))
        {
            prompt += " For chapter-summaries.md, read each chapter file in the chapters/ directory and write a 2-3 sentence summary for each chapter. " +
                "Format: ## Chapter N: Title\n\n followed by the summary.";
        }

        prompt += $" The working directory is {Path.Combine(libraryPath, slug)}";

        try
        {
            var sessionId = await agentService.EnsureSessionAsync(slug);
            await agentService.SendPromptAsync(sessionId, prompt);

            // Kill the write session
            try { await agentService.KillSessionAsync(sessionId); } catch { }

            // Update checkpoints based on what was actually created
            foreach (var file in expectedFiles)
            {
                var exists = File.Exists(Path.Combine(wikiDir, file));
                if (checkpoints.TryGetValue(file, out var cp))
                {
                    cp.Status = exists ? "done" : "failed";
                    cp.UpdatedAt = DateTime.UtcNow;
                }
                else if (exists)
                {
                    db.LoreCheckpoints.Add(new LoreCheckpoint
                    {
                        BookId = book.Id,
                        Slug = slug,
                        TargetFile = file,
                        Status = "done",
                        UpdatedAt = DateTime.UtcNow,
                    });
                }
            }
            await db.SaveChangesAsync();

            var doneCount = expectedFiles.Count(f => File.Exists(Path.Combine(wikiDir, f)));

            if (doneCount == 0)
            {
                book.Status = "error";
                book.ErrorMessage = "Lore generation completed but no wiki files were created.";
                book.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogError("Lore generation produced no files for {Slug}", slug);
                return;
            }

            book.Status = doneCount == expectedFiles.Length ? "lore-ready" : "error";
            book.ErrorMessage = doneCount == expectedFiles.Length
                ? null
                : $"Only {doneCount}/{expectedFiles.Length} wiki files generated. Retry to regenerate missing files.";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation("Lore generation complete for {Slug}: {Done}/{Total} wiki files",
                slug, doneCount, expectedFiles.Length);
        }
        catch (Exception ex)
        {
            // Mark pending checkpoints as failed
            foreach (var cp in checkpoints.Values.Where(c => c.Status == "pending"))
            {
                cp.Status = "failed";
                cp.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync();

            book.Status = "error";
            book.ErrorMessage = $"Lore generation failed: {ex.Message}";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogError(ex, "Lore generation failed for {Slug}", slug);
        }
    }
}
