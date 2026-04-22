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

    public Task<string> EnsureSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        var sessionId = $"test-session-{bookSlug}";
        CreatedSessions.Add(sessionId);
        SessionInfos[sessionId] = new SessionInfo(sessionId, bookSlug, "read-write", MessageCount, 0, 200000);
        return Task.FromResult(sessionId);
    }

    public Task<string> CreateNewSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        var sessionId = $"test-session-{bookSlug}-new-{Guid.NewGuid():N[..8]}";
        CreatedSessions.Add(sessionId);
        SessionInfos[sessionId] = new SessionInfo(sessionId, bookSlug, "read-write", 0, 0, 200000);
        return Task.FromResult(sessionId);
    }

    public Task<List<SessionSummary>> ListSessionsAsync(string bookSlug, CancellationToken ct = default)
    {
        var sessions = SessionInfos
            .Where(kvp => kvp.Value.BookSlug == bookSlug)
            .Select(kvp => new SessionSummary(kvp.Key, kvp.Value.BookSlug, "0s", "0s", 0))
            .ToList();
        return Task.FromResult(sessions);
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
        return Task.FromResult(new SessionInfo(sessionId, "unknown", "read-write", MessageCount, 0, 200000));
    }

    public Task CompactSessionAsync(string sessionId, string? customInstructions = null, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
