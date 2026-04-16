namespace KnowledgeEngine.Api.Services;

public class LoreService : ILoreService
{
    private readonly IAgentService _agent;
    private readonly IConfiguration _config;
    private readonly ILogger<LoreService> _logger;

    public LoreService(IAgentService agent, IConfiguration config, ILogger<LoreService> logger)
    {
        _agent = agent;
        _config = config;
        _logger = logger;
    }

    public async Task TriggerLoreGenerationAsync(string slug, CancellationToken ct = default)
    {
        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookMd = Path.Combine(libraryPath, slug, "book.md");

        if (!File.Exists(bookMd))
            throw new FileNotFoundException($"Book content not found: {bookMd}");

        var prompt = $"Read the book at {bookMd} and extract the lore. Generate a character list in {Path.Combine(libraryPath, slug, "wiki", "characters.md")}, locations in {Path.Combine(libraryPath, slug, "wiki", "locations.md")}, themes in {Path.Combine(libraryPath, slug, "wiki", "themes.md")}, and a plot summary in {Path.Combine(libraryPath, slug, "wiki", "summary.md")}.";

        _logger.LogInformation("Triggering lore generation for book: {Slug}", slug);

        try
        {
            await _agent.SendPromptAsync(prompt, ct);
            _logger.LogInformation("Lore generation complete for book: {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lore generation failed for book: {Slug}", slug);
            throw;
        }
    }
}
