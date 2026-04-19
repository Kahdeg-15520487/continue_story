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

        // Guard: already split (idempotent check)
        if (Directory.Exists(chaptersDir) && Directory.GetFiles(chaptersDir, "ch-*.md").Length > 0)
        {
            _logger.LogInformation("Chapters already exist for {Slug}, skipping split", slug);
            // Proceed directly to lore generation
            var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
            return;
        }

        Directory.CreateDirectory(chaptersDir);

        var prompt = $@"Read book.md and split it into chapter files in the chapters/ directory.

Rules:
- Detect chapter boundaries by headings (e.g. ## Chapter 1, # Part One, ## Chapter 2: The Journey, etc.)
- Each chapter gets its own file named ch-NNN-slugified-title.md (e.g. ch-001-the-awakening.md)
- Each file starts with the chapter heading as a level-1 heading (# Chapter Title)
- Include all content from that heading up to (but not including) the next chapter heading
- If no clear chapter structure is detected, create ch-001-untitled.md with the full content
- The chapter number (NNN) must be zero-padded to 3 digits
- Slugify the title: lowercase, spaces become hyphens, strip special chars
- Do NOT delete or modify book.md

The working directory is {Path.Combine(libraryPath, slug)}";

        try
        {
            var sessionId = await agentService.EnsureSessionAsync(slug, "write");
            await agentService.SendPromptAsync(sessionId, prompt);

            // Kill the write session
            try { await agentService.KillSessionAsync(sessionId); } catch { }

            // Verify chapters were created
            if (!Directory.Exists(chaptersDir) || Directory.GetFiles(chaptersDir, "ch-*.md").Length == 0)
            {
                _logger.LogError("Chapter split produced no files for {Slug}", slug);
                book.Status = "error";
                book.ErrorMessage = "Chapter splitting completed but no chapter files were created.";
                book.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                return;
            }

            var chapterCount = Directory.GetFiles(chaptersDir, "ch-*.md").Length;
            _logger.LogInformation("Split {Slug} into {Count} chapters", slug, chapterCount);

            // Now enqueue lore generation
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
}
