namespace KnowledgeEngine.Api.Services;

public interface IAgentService
{
    /// <summary>
    /// Send a non-streaming prompt to the Pi Agent and return the full response.
    /// </summary>
    Task<string> SendPromptAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Stream a prompt response from the Pi Agent.
    /// Yields JSON-RPC event strings as they arrive.
    /// </summary>
    IAsyncEnumerable<string> StreamPromptAsync(string message, CancellationToken ct = default);
}
