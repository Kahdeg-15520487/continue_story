using System.Formats.Tar;
using System.IO.Compression;

namespace StoryEngine.Api.Endpoints;

public static class ExportEndpoints
{
    private const string StagingFile = "/data/import-staging.tar.gz";

    public static void Map(WebApplication app)
    {
        // ── Export ─────────────────────────────────────────────────────

        app.MapGet("/api/export", async (
            IConfiguration config,
            HttpContext ctx) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var dbPath = config.GetValue<string>("SQLite:Path") ?? "/data";
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

            ctx.Response.ContentType = "application/gzip";
            ctx.Response.Headers.Append("Content-Disposition",
                $"attachment; filename=\"story-engine-export-{timestamp}.tar.gz\"");
            ctx.Response.Headers.Append("Cache-Control", "no-cache");

            await using var gzip = new GZipStream(ctx.Response.Body, CompressionLevel.Optimal, leaveOpen: true);
            await using var tar = new TarWriter(gzip, leaveOpen: true);

            // ── Add SQLite databases ──────────────────────────────────
            await AddFileToTar(tar, Path.Combine(dbPath, "knowledge-engine.db"), "knowledge-engine.db");
            await AddFileToTar(tar, Path.Combine(dbPath, "hangfire.db"), "hangfire.db");

            // ── Add library contents ───────────────────────────────────
            if (Directory.Exists(libraryPath))
            {
                foreach (var file in Directory.GetFiles(libraryPath, "*", SearchOption.AllDirectories))
                {
                    var relativePath = "library/" + Path.GetRelativePath(libraryPath, file)
                        .Replace('\\', '/');
                    await AddFileToTar(tar, file, relativePath);
                }
            }
        });

        // ── Import ─────────────────────────────────────────────────────

        app.MapPost("/api/import", async (HttpContext ctx) =>
        {
            if (!ctx.Request.HasFormContentType || !ctx.Request.Form.Files.Any())
                return Results.BadRequest(new { error = "Upload a .tar.gz file" });

            var file = ctx.Request.Form.Files[0];

            if (!file.FileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                && !file.FileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Expected a .tar.gz file" });

            // Save to staging — extracted on next startup
            Directory.CreateDirectory(Path.GetDirectoryName(StagingFile)!);
            await using var stream = file.OpenReadStream();
            await using var fs = File.Create(StagingFile);
            await stream.CopyToAsync(fs);

            return Results.Ok(new
            {
                imported = true,
                message = "Import staged. Restart the container to apply.",
                nextStep = "docker-compose restart api"
            });
        });
    }

    /// <summary>
    /// Extracts a staged import file if present. Call at startup before DB init.
    /// </summary>
    public static void ApplyStagedImport(IConfiguration config, ILogger logger)
    {
        if (!File.Exists(StagingFile))
            return;

        logger.LogInformation("Import staging file found, extracting...");

        var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
        var dbPath = config.GetValue<string>("SQLite:Path") ?? "/data";

        try
        {
            using var fs = File.OpenRead(StagingFile);
            using var gzip = new GZipStream(fs, CompressionMode.Decompress);
            using var reader = new TarReader(gzip, leaveOpen: true);

            while (reader.GetNextEntry() is { } entry)
            {
                var name = entry.Name.TrimStart('/');
                string targetPath;

                if (name.StartsWith("library/"))
                {
                    targetPath = Path.Combine(libraryPath, name["library/".Length..]);
                }
                else if (name == "knowledge-engine.db" || name == "hangfire.db")
                {
                    targetPath = Path.Combine(dbPath, name);
                }
                else
                {
                    logger.LogWarning("Import: skipping unknown entry {Name}", name);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

                if (entry.EntryType is TarEntryType.Directory)
                {
                    Directory.CreateDirectory(targetPath);
                    continue;
                }

                using var outStream = File.Create(targetPath);
                entry.DataStream!.CopyTo(outStream);
                logger.LogInformation("Import: extracted {Name} → {Path}", name, targetPath);
            }

            // Clean up staging file
            File.Delete(StagingFile);
            logger.LogInformation("Import complete. Staging file removed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import extraction failed. Staging file left at {Path}", StagingFile);
        }
    }

    private static async Task AddFileToTar(TarWriter tar, string sourcePath, string entryName)
    {
        if (!File.Exists(sourcePath))
            return;

        await using var fs = File.OpenRead(sourcePath);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = fs
        };
        await tar.WriteEntryAsync(entry);
    }
}
