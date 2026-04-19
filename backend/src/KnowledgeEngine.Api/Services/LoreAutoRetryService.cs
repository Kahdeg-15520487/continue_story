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

        // Only recover books stuck for at least 5 minutes
        var cutoff = DateTime.UtcNow.AddMinutes(-5);
        var stuck = await db.Books
            .Where(b => (b.Status == "generating-lore" || b.Status == "splitting" || b.Status == "error")
                && b.UpdatedAt < cutoff)
            .ToListAsync();

        if (stuck.Count == 0) return;

        var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();

        foreach (var book in stuck)
        {
            _logger.LogInformation("Auto-retrying stuck book: {Slug} (status={Status})",
                book.Slug, book.Status);

            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;

            // If stuck at splitting, re-queue split. Otherwise re-queue lore.
            if (book.Status == "splitting")
            {
                book.Status = "splitting";
                jobClient.Enqueue<ChapterSplitService>(x => x.SplitIntoChaptersAsync(book.Slug));
            }
            else
            {
                book.Status = "generating-lore";
                jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(book.Slug));
            }
        }

        await db.SaveChangesAsync();
        _logger.LogInformation("Re-queued lore generation for {Count} stuck books", stuck.Count);
    }
}
