namespace KnowledgeEngine.Api.Services;

public interface IAgentService
{
    /// <summary>
    /// Ensure an agent session exists for the given book, returning the session ID.
    /// </summary>
    Task<string> EnsureSessionAsync(string bookSlug, string mode = "read", CancellationToken ct = default);

    /// <summary>
    /// Send a non-streaming prompt to a session and return the full response.
    /// </summary>
    Task<string> SendPromptAsync(string sessionId, string message, CancellationToken ct = default);

    /// <summary>
    /// Stream a prompt response from a session.
    /// Yields JSON-RPC event strings as they arrive.
    /// </summary>
    IAsyncEnumerable<string> StreamPromptAsync(string sessionId, string message, CancellationToken ct = default);

    /// <summary>
    /// Kill a session by ID.
    /// </summary>
    Task KillSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Get session info (message count, token usage, etc).
    /// </summary>
    Task<SessionInfo> GetSessionInfoAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Trigger compaction on a session.
    /// </summary>
    Task CompactSessionAsync(string sessionId, string? customInstructions = null, CancellationToken ct = default);
}

public record SessionInfo(string SessionId, string BookSlug, string Mode, int MessageCount, long TokenUsed, long TokenLimit);
