using StoryEngine.Api.Services;

namespace StoryEngine.Api.Endpoints;

public static class SettingsEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/settings/llm");

        group.MapGet("/", async (IAgentService agent, CancellationToken ct) =>
        {
            var result = await agent.GetLlmSettingsAsync(ct);
            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
        });

        group.MapPost("/discover", async (HttpContext ctx, IAgentService agent, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var result = await agent.DiscoverModelsAsync(body, ct);
            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
        });

        group.MapPost("/providers", async (HttpContext ctx, IAgentService agent, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var result = await agent.SaveProviderAsync(body, ct);
            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
        });

        group.MapDelete("/providers/{id}", async (string id, IAgentService agent, CancellationToken ct) =>
        {
            var result = await agent.DeleteProviderAsync(id, ct);
            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
        });

        group.MapPost("/providers/{id}/test", async (string id, IAgentService agent, CancellationToken ct) =>
        {
            var result = await agent.TestProviderAsync(id, ct);
            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
        });

        group.MapPut("/default", async (HttpContext ctx, IAgentService agent, CancellationToken ct) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ct);
            var result = await agent.SetDefaultModelAsync(body, ct);
            return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
        });
    }
}
