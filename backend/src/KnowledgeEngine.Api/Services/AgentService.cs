using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KnowledgeEngine.Api.Services;

public class AgentService : IAgentService
{
    private readonly HttpClient _http;
    private readonly string _agentBaseUrl;
    private readonly ILogger<AgentService> _logger;

    public AgentService(HttpClient http, IConfiguration config, ILogger<AgentService> logger)
    {
        _http = http;
        _logger = logger;
        var host = config.GetValue<string>("Agent:Host") ?? "agent";
        var port = config.GetValue<int>("Agent:Port");
        if (port == 0) port = 3001;
        _agentBaseUrl = $"http://{host}:{port}";
        _http.Timeout = TimeSpan.FromMinutes(10);
        _logger.LogInformation("AgentService configured: {Url}", _agentBaseUrl);
    }

    public async Task<string> EnsureSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring session for book: {Slug}", bookSlug);

        var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions",
            new StringContent(JsonSerializer.Serialize(new { bookSlug }), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var sessionId = result.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Agent returned no sessionId");

        _logger.LogInformation("Session ready: {SessionId} for {Slug}", sessionId, bookSlug);
        return sessionId;
    }

    public async Task<string> SendPromptAsync(string sessionId, string message, CancellationToken ct = default)
    {
        _logger.LogInformation("Sending prompt to session {Session} ({Length} chars)", sessionId, message.Length);

        try
        {
            var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions/{sessionId}/prompt",
                new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (result.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
                return data.ToString() ?? "";

            _logger.LogWarning("Agent returned no data for session {Session}", sessionId);
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send prompt to session {Session}", sessionId);
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(string sessionId, string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Streaming to session {Session} ({Length} chars)", sessionId, message.Length);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_agentBaseUrl}/api/sessions/{sessionId}/prompt/stream");
        request.Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("data: "))
                yield return line["data: ".Length..];
        }
    }

    public async Task KillSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _http.DeleteAsync($"{_agentBaseUrl}/api/sessions/{sessionId}", ct);
        }
        catch { }
    }

    public async Task<SessionInfo> GetSessionInfoAsync(string sessionId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{_agentBaseUrl}/api/sessions/{sessionId}/info", ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);

        return new SessionInfo(
            result.GetProperty("sessionId").GetString() ?? sessionId,
            result.GetProperty("bookSlug").GetString() ?? "",
            result.GetProperty("mode").GetString() ?? "read",
            result.GetProperty("messageCount").GetInt32(),
            result.GetProperty("tokenBudget").GetProperty("used").GetInt64(),
            result.GetProperty("tokenBudget").GetProperty("limit").GetInt64()
        );
    }

    public async Task CompactSessionAsync(string sessionId, string? customInstructions = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions/{sessionId}/compact",
            new StringContent(JsonSerializer.Serialize(new { customInstructions }), Encoding.UTF8, "application/json"),
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> CreateNewSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        _logger.LogInformation("Creating new session for book: {Slug}", bookSlug);

        // Kill any existing in-memory sessions for this book first
        var existing = await ListSessionsAsync(bookSlug, ct);
        foreach (var s in existing)
        {
            try { await KillSessionAsync(s.Id, ct); } catch { }
        }

        var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions",
            new StringContent(JsonSerializer.Serialize(new { bookSlug, forceNew = true }), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        return result.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Agent returned no sessionId");
    }

    public async Task<List<SessionSummary>> ListSessionsAsync(string bookSlug, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_agentBaseUrl}/api/books/{bookSlug}/sessions", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var sessions = result.GetProperty("sessions");

            var list = new List<SessionSummary>();
            foreach (var s in sessions.EnumerateArray())
            {
                list.Add(new SessionSummary(
                    s.GetProperty("id").GetString()!,
                    s.GetProperty("bookSlug").GetString()!,
                    s.GetProperty("age").GetString()!,
                    s.GetProperty("idle").GetString()!,
                    s.GetProperty("tokenCount").GetInt32()
                ));
            }
            return list;
        }
        catch
        {
            return new List<SessionSummary>();
        }
    }
}
