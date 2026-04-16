namespace KnowledgeEngine.Api.Services;

public interface ILoreService
{
    Task TriggerLoreGenerationAsync(string slug, CancellationToken ct = default);
}
