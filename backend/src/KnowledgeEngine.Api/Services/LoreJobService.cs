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

    // Hangfire job — triggers lore generation
    public async Task GenerateLoreAsync(string slug)
    {
        // Create a new scope for Hangfire background execution
        using var scope = _scopeFactory.CreateScope();
        var agentService = scope.ServiceProvider.GetRequiredService<IAgentService>();

        _logger.LogInformation("Generating lore for book: Slug={Slug}", slug);

        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookMd = Path.Combine(libraryPath, slug, "book.md");

        if (!File.Exists(bookMd) || new FileInfo(bookMd).Length == 0)
        {
            _logger.LogError("Book content not found or empty: {BookPath}", bookMd);
            // Update book status to error
            var db = scope.ServiceProvider.GetRequiredService<KnowledgeEngine.Api.Data.AppDbContext>();
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is not null)
            {
                book.Status = "error";
                book.ErrorMessage = "Cannot generate lore: book has no content";
                book.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            return;
        }

        var prompt = $"Read the book at {bookMd} and extract the lore. Generate a character list in {Path.Combine(libraryPath, slug, "wiki", "characters.md")}, locations in {Path.Combine(libraryPath, slug, "wiki", "locations.md")}, themes in {Path.Combine(libraryPath, slug, "wiki", "themes.md")}, and a plot summary in {Path.Combine(libraryPath, slug, "wiki", "summary.md")}.";

        try
        {
            var sessionId = await agentService.EnsureSessionAsync(slug, "write");
            await agentService.SendPromptAsync(sessionId, prompt);
            _logger.LogInformation("Lore generation complete for book: {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lore generation failed for book: {Slug}", slug);
            throw;
        }
    }
}
