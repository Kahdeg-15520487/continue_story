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
        _logger.LogInformation("AgentService configured: {Url}", _agentBaseUrl);
    }

    public async Task<string> SendPromptAsync(string message, CancellationToken ct = default)
    {
        _logger.LogInformation("Sending non-streaming prompt to agent ({Length} chars)", message.Length);

        try
        {
            var response = await _http.PostAsync($"{_agentBaseUrl}/api/prompt",
                new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (result.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
                return data.ToString() ?? "";

            _logger.LogWarning("Agent returned no data for prompt");
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send prompt to agent");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Sending streaming prompt to agent ({Length} chars)", message.Length);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_agentBaseUrl}/api/prompt/stream");
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
}
