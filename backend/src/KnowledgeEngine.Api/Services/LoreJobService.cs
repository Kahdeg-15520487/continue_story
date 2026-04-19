using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Services;

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
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeEngine.Api.Data.AppDbContext>();

        _logger.LogInformation("Generating lore for book: Slug={Slug}", slug);

        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookMd = Path.Combine(libraryPath, slug, "book.md");

        var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
        if (book is null)
        {
            _logger.LogError("Book not found in DB: {Slug}", slug);
            return;
        }

        // Guard: already done (Hangfire retry after success)
        var wikiDir = Path.Combine(libraryPath, slug, "wiki");
        if (book.Status == "lore-ready" && Directory.Exists(wikiDir)
            && Directory.GetFiles(wikiDir, "*.md").Length > 0)
        {
            _logger.LogInformation("Lore already generated for {Slug}, skipping", slug);
            return;
        }

        // Guard: currently generating (Hangfire retry while first attempt is still running)
        if (book.Status == "generating-lore")
        {
            _logger.LogInformation("Lore already being generated for {Slug}, skipping retry", slug);
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

        book.Status = "generating-lore";
        book.ErrorMessage = null;
        book.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var prompt = $"Read the book at book.md and extract lore using the lore-extraction skill. " +
            $"Generate wiki files in the wiki/ directory: characters.md, locations.md, themes.md, and summary.md. " +
            $"Follow the skill's output format exactly. The working directory is {Path.Combine(libraryPath, slug)}";

        try
        {
            var sessionId = await agentService.EnsureSessionAsync(slug, "write");
            await agentService.SendPromptAsync(sessionId, prompt);

            var expectedFiles = new[] { "characters.md", "locations.md", "themes.md", "summary.md" };
            var createdFiles = expectedFiles
                .Where(f => File.Exists(Path.Combine(wikiDir, f)))
                .ToList();

            if (createdFiles.Count == 0)
            {
                book.Status = "error";
                book.ErrorMessage = "Lore generation completed but no wiki files were created. The agent may not have followed instructions.";
                book.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogError("Lore generation produced no files for {Slug}", slug);
                return;
            }

            book.Status = "lore-ready";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogInformation("Lore generation complete for {Slug}: {Count} wiki files ({Files})",
                slug, createdFiles.Count, string.Join(", ", createdFiles));
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
