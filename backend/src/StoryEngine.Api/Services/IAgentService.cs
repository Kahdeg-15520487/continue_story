namespace StoryEngine.Api.Services;

public interface IAgentService
{
    /// <summary>
    /// Ensure a durable conversation exists for the given book (or "__shared__"), returning its ID.
    /// </summary>
    Task<string> EnsureSessionAsync(string bookSlug, CancellationToken ct = default);

    /// <summary>
    /// Reset the book's conversation (new context, history kept) and return its ID.
    /// </summary>
    Task<string> CreateNewSessionAsync(string bookSlug, CancellationToken ct = default);

    Task<List<SessionSummary>> ListSessionsAsync(string bookSlug, CancellationToken ct = default);

    /// <summary>
    /// Send a non-streaming prompt and return the full response.
    /// </summary>
    Task<string> SendPromptAsync(string sessionId, string message, CancellationToken ct = default);

    /// <summary>
    /// Attach to the durable conversation stream, submit the input, and yield
    /// server-sent-event payloads until the run settles.
    /// </summary>
    IAsyncEnumerable<string> StreamPromptAsync(string sessionId, string message, CancellationToken ct = default);

    /// <summary>
    /// Unmap a conversation (the transcript remains in durable storage).
    /// </summary>
    Task KillSessionAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Start a new context in the conversation (pi-durable <c>reset</c>).
    /// </summary>
    Task ResetSessionAsync(string sessionId, string? handoff = null, CancellationToken ct = default);

    /// <summary>
    /// Abort the running turn and return the last user message.
    /// </summary>
    Task<string?> AbortSessionAsync(string sessionId, CancellationToken ct = default);

    Task<string?> GetLastUserMessageAsync(string bookSlug, CancellationToken ct = default);

    Task<SessionInfo> GetSessionInfoAsync(string sessionId, CancellationToken ct = default);

    Task CompactSessionAsync(string sessionId, string? customInstructions = null, CancellationToken ct = default);

    /// <summary>
    /// Raw entries page from the durable transcript (for ConversationTranscript).
    /// </summary>
    Task<string> GetEntriesAsync(string sessionId, int limit = 50, long? after = null, long? before = null, string? order = null, CancellationToken ct = default);

    /// <summary>LLM provider settings (keys masked) from the agent.</summary>
    Task<AgentResponse> GetLlmSettingsAsync(CancellationToken ct = default);
    Task<AgentResponse> DiscoverModelsAsync(string json, CancellationToken ct = default);
    Task<AgentResponse> ConfigureConversationAsync(string sessionId, string json, CancellationToken ct = default);
    Task<AgentResponse> GetConversationAsync(string sessionId, CancellationToken ct = default);
    Task<AgentResponse> SaveProviderAsync(string json, CancellationToken ct = default);
    Task<AgentResponse> DeleteProviderAsync(string providerId, CancellationToken ct = default);
    Task<AgentResponse> TestProviderAsync(string providerId, CancellationToken ct = default);
    Task<AgentResponse> SetDefaultModelAsync(string json, CancellationToken ct = default);
}

public record SessionInfo(string SessionId, string BookSlug, string Mode, int MessageCount, long TokenUsed, long TokenLimit);
public record SessionSummary(string Id, string BookSlug, string Age, string Idle, int TokenCount);

public record AgentResponse(int StatusCode, string Body);
