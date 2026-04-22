namespace KnowledgeEngine.Api.Services;

public class ScratchCleanupService : IHostedService
{
    private readonly ILogger<ScratchCleanupService> _logger;
    private readonly IConfiguration _config;

    public ScratchCleanupService(
        ILogger<ScratchCleanupService> logger,
        IConfiguration config)
    {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";

        if (!Directory.Exists(libraryPath))
            return Task.CompletedTask;

        var cutoff = DateTime.UtcNow.AddHours(-1);
        var cleanedCount = 0;

        foreach (var chaptersDir in Directory.GetDirectories(libraryPath, "chapters", SearchOption.AllDirectories))
        {
            var scratchFiles = Directory.GetFiles(chaptersDir, "*.scratch.md");
            foreach (var file in scratchFiles)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.LastWriteTimeUtc < cutoff)
                    {
                        File.Delete(file);
                        cleanedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete scratch file: {File}", file);
                }
            }
        }

        if (cleanedCount > 0)
            _logger.LogInformation("Scratch cleanup: deleted {Count} stale scratch files (>1 hour old)", cleanedCount);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
