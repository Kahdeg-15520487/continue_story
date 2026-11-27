using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace StoryEngine.Api.Services;

public class AgentService : IAgentService
{
    private const string SharedSlug = "__shared__";

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

    private static string ModeFor(string bookSlug) => bookSlug == SharedSlug ? "shared" : "story";

    public async Task<string> EnsureSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"{_agentBaseUrl}/api/conversations",
            Json(new { slug = bookSlug, mode = ModeFor(bookSlug) }),
            ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var id = doc.RootElement.GetProperty("conversationId").GetInt64();
        _logger.LogInformation("Conversation {ConversationId} ready for {Slug} ({Mode})", id, bookSlug, ModeFor(bookSlug));
        return id.ToString();
    }

    public async Task<string> CreateNewSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        var sessionId = await EnsureSessionAsync(bookSlug, ct);
        await ResetSessionAsync(sessionId, null, ct);
        return sessionId;
    }

    public async Task ResetSessionAsync(string sessionId, string? handoff = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"{_agentBaseUrl}/api/conversations/{sessionId}/reset",
            Json(new { handoff }),
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<string> SendPromptAsync(string sessionId, string message, CancellationToken ct = default)
    {
        _logger.LogInformation("Sending prompt to conversation {Conversation} ({Length} chars)", sessionId, message.Length);
        var response = await _http.PostAsync(
            $"{_agentBaseUrl}/api/conversations/{sessionId}/prompt",
            Json(new { content = message }),
            ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String
            ? data.GetString() ?? ""
            : "";
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(
        string sessionId,
        string message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Streaming to conversation {Conversation} ({Length} chars)", sessionId, message.Length);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_agentBaseUrl}/api/conversations/{sessionId}/stream");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var assistantBaseline = -1;
        var sawRun = false;
        var submitted = false;
        var done = false;

        while (!done)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (!line.StartsWith("data: ")) continue;

            var evt = line["data: ".Length..];
            yield return evt;

            if (!TryParseFrame(evt, out var kind, out var hasRun, out var failed, out var assistantCount))
                continue;
            if (kind == "end") break;
            if (kind != "value") continue;

            if (assistantBaseline < 0) assistantBaseline = assistantCount;

            if (!submitted)
            {
                await SubmitInputAsync(sessionId, message, ct);
                submitted = true;
            }

            if (hasRun) sawRun = true;
            if (failed || (sawRun && !hasRun) || (submitted && assistantCount > assistantBaseline && !hasRun))
                done = true;
        }
    }

    public async Task KillSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _http.DeleteAsync($"{_agentBaseUrl}/api/conversations/{sessionId}", ct);
        }
        catch { }
    }

    public async Task<string?> AbortSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsync(
                $"{_agentBaseUrl}/api/conversations/{sessionId}/abort",
                Json(new { }),
                ct);
            response.EnsureSuccessStatusCode();
            return await LastUserMessageAsync(sessionId, ct);
        }
        catch { return null; }
    }

    public async Task<string?> GetLastUserMessageAsync(string bookSlug, CancellationToken ct = default)
    {
        try
        {
            var sessionId = await EnsureSessionAsync(bookSlug, ct);
            return await LastUserMessageAsync(sessionId, ct);
        }
        catch { return null; }
    }

    public async Task<SessionInfo> GetSessionInfoAsync(string sessionId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"{_agentBaseUrl}/api/conversations/{sessionId}", ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var key = root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";
        var messageCount = root.TryGetProperty("messageCount", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0;

        long tokens = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object)
        {
            foreach (var model in models.EnumerateObject())
            {
                if (model.Value.ValueKind == JsonValueKind.Object
                    && model.Value.TryGetProperty("totalTokens", out var t)
                    && t.ValueKind == JsonValueKind.Number
                    && t.TryGetInt64(out var value))
                    tokens += value;
            }
        }

        var mode = key == SharedSlug ? "shared" : "story";
        return new SessionInfo(sessionId, key, mode, messageCount, tokens, 100_000);
    }

    public async Task CompactSessionAsync(string sessionId, string? customInstructions = null, CancellationToken ct = default)
    {
        var response = await _http.PostAsync(
            $"{_agentBaseUrl}/api/conversations/{sessionId}/compact",
            Json(new { instructions = customInstructions }),
            ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<SessionSummary>> ListSessionsAsync(string bookSlug, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_agentBaseUrl}/api/conversations", ct);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var list = new List<SessionSummary>();
            if (!doc.RootElement.TryGetProperty("conversations", out var conversations))
                return list;

            foreach (var conversation in conversations.EnumerateArray())
            {
                var key = conversation.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                    ? k.GetString()
                    : null;
                if (key != bookSlug) continue;
                var id = conversation.GetProperty("id").GetInt64().ToString();
                list.Add(new SessionSummary(id, bookSlug, "", "", 0));
            }
            return list;
        }
        catch
        {
            return new List<SessionSummary>();
        }
    }

    public async Task<string> GetEntriesAsync(
        string sessionId,
        int limit = 50,
        long? after = null,
        long? before = null,
        string? order = null,
        CancellationToken ct = default)
    {
        var query = $"?limit={limit}";
        if (after.HasValue) query += $"&after={after.Value}";
        if (before.HasValue) query += $"&before={before.Value}";
        if (!string.IsNullOrEmpty(order)) query += $"&order={order}";

        var response = await _http.GetAsync($"{_agentBaseUrl}/api/conversations/{sessionId}/entries{query}", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public Task<AgentResponse> GetLlmSettingsAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Get, "/api/settings/llm", null, ct);

    public Task<AgentResponse> DiscoverModelsAsync(string json, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/settings/llm/discover", json, ct);

    public Task<AgentResponse> SaveProviderAsync(string json, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/settings/llm/providers", json, ct);

    public Task<AgentResponse> DeleteProviderAsync(string providerId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"/api/settings/llm/providers/{providerId}", null, ct);

    public Task<AgentResponse> TestProviderAsync(string providerId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/api/settings/llm/providers/{providerId}/test", "{}", ct);

    public Task<AgentResponse> SetDefaultModelAsync(string json, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, "/api/settings/llm/default", json, ct);

    private async Task<AgentResponse> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"{_agentBaseUrl}{path}");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return new AgentResponse((int)response.StatusCode, text);
    }

    // ── helpers ────────────────────────────────────────────────────────

    private async Task SubmitInputAsync(string sessionId, string message, CancellationToken ct)
    {
        var response = await _http.PostAsync(
            $"{_agentBaseUrl}/api/conversations/{sessionId}/input",
            Json(new { content = message }),
            ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string?> LastUserMessageAsync(string sessionId, CancellationToken ct)
    {
        var response = await _http.GetAsync(
            $"{_agentBaseUrl}/api/conversations/{sessionId}/entries?limit=100&order=descending",
            ct);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("kind", out var kind) || kind.GetString() != "pi.user") continue;
            if (!item.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.Array) continue;
            var first = model.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("content", out var content))
                return ExtractText(content);
        }
        return null;
    }

    private static bool TryParseFrame(
        string json,
        out string kind,
        out bool hasRun,
        out bool failed,
        out int assistantCount)
    {
        kind = "";
        hasRun = false;
        failed = false;
        assistantCount = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("kind", out var kindEl)) return false;
            kind = kindEl.GetString() ?? "";
            if (kind != "value") return true;
            if (!root.TryGetProperty("value", out var value)) return true;

            if (value.TryGetProperty("docs", out var docs)
                && docs.TryGetProperty("pi.live", out var live)
                && live.ValueKind == JsonValueKind.Object)
            {
                hasRun = live.TryGetProperty("run", out var run) && run.ValueKind != JsonValueKind.Null;
                failed = live.TryGetProperty("runFailure", out var runFailure) && runFailure.ValueKind != JsonValueKind.Null;
            }

            if (value.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (entry.TryGetProperty("kind", out var entryKind) && entryKind.GetString() == "pi.assistant")
                        assistantCount++;
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? "";
        if (element.ValueKind != JsonValueKind.Array) return "";

        var sb = new StringBuilder();
        foreach (var block in element.EnumerateArray())
        {
            if (block.ValueKind == JsonValueKind.String)
            {
                sb.Append(block.GetString());
                continue;
            }
            if (block.ValueKind == JsonValueKind.Object
                && block.TryGetProperty("type", out var type) && type.GetString() == "text"
                && block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            {
                sb.Append(text.GetString());
            }
        }
        return sb.ToString();
    }

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
