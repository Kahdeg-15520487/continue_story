using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KnowledgeEngine.Api.Services;

public class AgentService : IAgentService
{
    private readonly HttpClient _http;
    private readonly ILogger<AgentService> _logger;
    private readonly string _agentBaseUrl;

    public AgentService(HttpClient http, IConfiguration config, ILogger<AgentService> logger)
    {
        _http = http;
        _logger = logger;
        var host = config.GetValue<string>("Agent:Host") ?? "agent";
        var port = config.GetValue<int>("Agent:Port");
        _agentBaseUrl = $"http://{host}:{port}";
    }

    public async Task<string> SendPromptAsync(string message, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"{_agentBaseUrl}/api/prompt",
            new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        return result.GetProperty("data").ToString();
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
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
