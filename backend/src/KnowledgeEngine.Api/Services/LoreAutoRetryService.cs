using Hangfire;
using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;

namespace KnowledgeEngine.Api.Services;

/// <summary>
/// Scans for books stuck in generating-lore or error status and re-queues lore generation.
/// Runs on startup and every 10 minutes via Hangfire recurring job.
/// </summary>
[Hangfire.AutomaticRetry(Attempts = 0)]
public class LoreAutoRetryService
{
    private readonly ILogger<LoreAutoRetryService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public LoreAutoRetryService(ILogger<LoreAutoRetryService> logger, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    public async Task RecoverStuckAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var stuck = await db.Books
            .Where(b => b.Status == "generating-lore" || b.Status == "error")
            .ToListAsync();

        if (stuck.Count == 0) return;

        var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();

        foreach (var book in stuck)
        {
            _logger.LogInformation("Auto-retrying lore for stuck book: {Slug} (status={Status})",
                book.Slug, book.Status);

            book.Status = "generating-lore";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;

            jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(book.Slug));
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Re-queued lore generation for {Count} stuck books", stuck.Count);
    }
}
