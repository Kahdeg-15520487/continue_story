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

    private static readonly string[] WikiCategories = ["characters", "locations"];

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
        var bookMd = Path.Combine(libraryPath, slug, "book.org.md");
        var wikiDir = Path.Combine(libraryPath, slug, "wiki");

        var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
        if (book is null)
        {
            _logger.LogError("Book not found in DB: {Slug}", slug);
            return;
        }

        // Guard: already done
        if (book.Status == "lore-ready" && Directory.Exists(wikiDir)
            && Directory.GetDirectories(wikiDir).Length > 0)
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
            _logger.LogError("Lore generation skipped: no book.org.md for {Slug}", slug);
            return;
        }

        // Create wiki subdirectories
        foreach (var cat in WikiCategories)
            Directory.CreateDirectory(Path.Combine(wikiDir, cat));

        book.Status = "generating-lore";
        book.ErrorMessage = null;
        book.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var prompt = $"Read the book at book.org.md (the immutable original source — do NOT modify it) and extract lore using the lore-extraction skill. " +
            $"Create individual entity files in wiki/characters/ and wiki/locations/ directories (one file per entity). " +
            $"Also create wiki/summary.md for the plot summary. " +
            $"Follow the skill's output format exactly. " +
            $"The working directory is {Path.Combine(libraryPath, slug)}";

        try
        {
            var sessionId = await agentService.EnsureSessionAsync(slug);
            await agentService.SendPromptAsync(sessionId, prompt);

            try { await agentService.KillSessionAsync(sessionId); } catch { }

            // Count what was created
            var totalEntities = 0;
            foreach (var cat in WikiCategories)
            {
                var catDir = Path.Combine(wikiDir, cat);
                if (Directory.Exists(catDir))
                    totalEntities += Directory.GetFiles(catDir, "*.md").Length;
            }

            var hasSummary = File.Exists(Path.Combine(wikiDir, "summary.md"));

            if (totalEntities == 0 && !hasSummary)
            {
                // Don't error out — book works fine without lore, just mark ready
                book.Status = "ready";
                book.ErrorMessage = null;
                book.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogWarning("Lore generation produced no files for {Slug}, marking as ready", slug);
                return;
            }

            book.Status = "lore-ready";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            _logger.LogInformation("Lore generation complete for {Slug}: {Count} entities, summary={HasSummary}",
                slug, totalEntities, hasSummary);
        }
        catch (Exception ex)
        {
            book.Status = "error";
            book.ErrorMessage = $"Lore generation failed: {ex.Message}";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogError(ex, "Lore generation failed for {Slug}", slug);
        }
    }

}
