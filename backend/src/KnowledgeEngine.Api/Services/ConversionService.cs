using System.Diagnostics;

namespace KnowledgeEngine.Api.Services;

public class ConversionService : IConversionService
{
    private readonly ILogger<ConversionService> _logger;

    public ConversionService(ILogger<ConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ConvertToMarkdownAsync(string inputPath, string outputPath)
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

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            _logger.LogError("markitdown failed (exit {Code}): {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"markitdown conversion failed: {error}");
        }

        await File.WriteAllTextAsync(outputPath, output);

        _logger.LogInformation("Conversion complete: {Output} ({Length} chars)", outputPath, output.Length);
        return outputPath;
    }
}
