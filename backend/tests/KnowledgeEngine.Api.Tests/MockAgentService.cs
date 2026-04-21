using System.Collections.Concurrent;
using KnowledgeEngine.Api.Services;

namespace KnowledgeEngine.Api.Tests;

public class MockAgentService : IAgentService
{
    public ConcurrentBag<string> SentPrompts { get; } = [];
    public ConcurrentBag<string> CreatedSessions { get; } = [];
    public ConcurrentBag<string> KilledSessions { get; } = [];
    public ConcurrentDictionary<string, SessionInfo> SessionInfos { get; } = [];

    public int MessageCount { get; set; } = 0;

    public Task<string> EnsureSessionAsync(string bookSlug, string mode = "read", CancellationToken ct = default)
    {
        var sessionId = $"test-session-{bookSlug}-{mode}";
        CreatedSessions.Add(sessionId);
        SessionInfos[sessionId] = new SessionInfo(sessionId, bookSlug, mode, MessageCount, 0, 200000);
        return Task.FromResult(sessionId);
    }

    public Task<string> SendPromptAsync(string sessionId, string message, CancellationToken ct = default)
    {
        SentPrompts.Add($"[{sessionId}] {message}");
        return Task.FromResult("Mock agent response");
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(string sessionId, string message, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        SentPrompts.Add($"[{sessionId}] {message}");
        yield return "event: message_start\ndata: {}\n\n";
        yield return "event: content_block_delta\ndata: {\"type\":\"text_delta\",\"text\":\"Mock agent response\"}\n\n";
        yield return "event: message_end\ndata: {\"message\":{\"usage\":{\"output_tokens\":10}}}\n\n";
    }

    public Task KillSessionAsync(string sessionId, CancellationToken ct = default)
    {
        KilledSessions.Add(sessionId);
        return Task.CompletedTask;
    }

    public Task<SessionInfo> GetSessionInfoAsync(string sessionId, CancellationToken ct = default)
    {
        if (SessionInfos.TryGetValue(sessionId, out var info))
            return Task.FromResult(info);
        return Task.FromResult(new SessionInfo(sessionId, "unknown", "read", MessageCount, 0, 200000));
    }

    public Task CompactSessionAsync(string sessionId, string? customInstructions = null, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
