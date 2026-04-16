using Hangfire;
using KnowledgeEngine.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Services;

public class ConversionJobService
{
    private readonly ILogger<ConversionJobService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public ConversionJobService(
        ILogger<ConversionJobService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    // Hangfire continuation job — runs after conversion completes
    public async Task UpdateBookAfterConversion(int bookId)
    {
        // Create a new scope for Hangfire background execution
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("Updating book status after conversion: BookId={BookId}", bookId);

        var book = await db.Books.FindAsync(bookId);
        if (book is null)
        {
            _logger.LogWarning("Book not found: BookId={BookId}", bookId);
            return;
        }

        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookDir = Path.Combine(libraryPath, book.Slug);
        var outputPath = Path.Combine(bookDir, "book.md");

        if (File.Exists(outputPath))
        {
            book.Status = "ready";
            book.UpdatedAt = DateTime.UtcNow;
            _logger.LogInformation("Book marked as ready: Slug={Slug}", book.Slug);
        }
        else
        {
            book.Status = "error";
            book.ErrorMessage = "Output file was not created";
            book.UpdatedAt = DateTime.UtcNow;
            _logger.LogError("Book marked as error (output file not found): Slug={Slug}", book.Slug);
        }

        await db.SaveChangesAsync();
    }
}
