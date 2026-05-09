namespace StoryEngine.Api.Services;

public interface IConversionService
{
    Task<string> ConvertToMarkdownAsync(string inputPath, string outputPath, CancellationToken ct = default);
}
