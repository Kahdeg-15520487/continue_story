using System.Diagnostics;
using Hangfire;
using Hangfire.Common;

namespace KnowledgeEngine.Api.Services;

[AutomaticRetry(Attempts = 0)]
public class ConversionService : IConversionService
{
    private readonly ILogger<ConversionService> _logger;

    public ConversionService(ILogger<ConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ConvertToMarkdownAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input file not found: {inputPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "markitdown",
            Arguments = $"\"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Starting conversion: {Input} -> {Output}", inputPath, outputPath);

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("markitdown failed (exit {Code}): {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"markitdown conversion failed: {error}");
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogWarning("markitdown produced empty output for: {Input}", inputPath);
            // Write empty file so the continuation can detect it
            await File.WriteAllTextAsync(outputPath, "", ct);
            return outputPath;
        }

        await File.WriteAllTextAsync(outputPath, output, ct);

        _logger.LogInformation("Conversion complete: {Output} ({Length} chars)", outputPath, output.Length);
        return outputPath;
    }
}
