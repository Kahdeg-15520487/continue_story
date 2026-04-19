using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Services;

[Hangfire.AutomaticRetry(Attempts = 0)]
public class SessionCleanupService
{
    private readonly ILogger<SessionCleanupService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public SessionCleanupService(
        ILogger<SessionCleanupService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    public async Task CleanupAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("Starting session cleanup...");

        // 1. Clear completed checkpoints for lore-ready books
        var loreReadyBookIds = await db.Books
            .Where(b => b.Status == "lore-ready")
            .Select(b => b.Id)
            .ToListAsync();

        if (loreReadyBookIds.Count > 0)
        {
            var completedCheckpoints = await db.LoreCheckpoints
                .Where(c => loreReadyBookIds.Contains(c.BookId) && c.Status == "done")
                .ToListAsync();

            if (completedCheckpoints.Count > 0)
            {
                db.LoreCheckpoints.RemoveRange(completedCheckpoints);
                await db.SaveChangesAsync();
                _logger.LogInformation("Cleaned up {Count} completed lore checkpoints", completedCheckpoints.Count);
            }
        }

        // 2. Expire old agent tasks (>24 hours)
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var oldTasks = await db.AgentTasks
            .Where(t => t.Status != "done" && t.Status != "expired" && t.UpdatedAt < cutoff)
            .ToListAsync();

        foreach (var task in oldTasks)
        {
            task.Status = "expired";
            task.UpdatedAt = DateTime.UtcNow;
        }

        if (oldTasks.Count > 0)
        {
            await db.SaveChangesAsync();
            _logger.LogInformation("Expired {Count} old agent tasks", oldTasks.Count);
        }

        // 3. Clean up stale .pi-sessions files (>7 days)
        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        if (Directory.Exists(libraryPath))
        {
            var sessionDirs = Directory.GetDirectories(libraryPath, ".pi-sessions", SearchOption.AllDirectories);
            var staleCutoff = DateTime.UtcNow.AddDays(-7);
            var cleanedFiles = 0;

            foreach (var sessionDir in sessionDirs)
            {
                try
                {
                    var files = Directory.GetFiles(sessionDir, "*.jsonl");
                    foreach (var file in files)
                    {
                        var lastWrite = File.GetLastWriteTimeUtc(file);
                        if (lastWrite < staleCutoff)
                        {
                            File.Delete(file);
                            cleanedFiles++;
                        }
                    }

                    // Remove empty session directories
                    if (!Directory.EnumerateFileSystemEntries(sessionDir).Any())
                    {
                        Directory.Delete(sessionDir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to clean session dir: {Dir}", sessionDir);
                }
            }

            if (cleanedFiles > 0)
            {
                _logger.LogInformation("Cleaned up {Count} stale session files (>7 days)", cleanedFiles);
            }
        }

        _logger.LogInformation("Session cleanup complete");
    }
}
