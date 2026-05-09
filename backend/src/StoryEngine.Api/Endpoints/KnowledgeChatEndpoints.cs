using System.Text.Json;
using StoryEngine.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace StoryEngine.Api.Endpoints;

public static class KnowledgeChatEndpoints
{
    private const string SHARED_SLUG = "__shared__";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/knowledge/chat");

        // Get or create session
        group.MapGet("/session", async (IAgentService agentService) =>
        {
            try
            {
                var sessionId = await agentService.EnsureSessionAsync(SHARED_SLUG);
                var info = await agentService.GetSessionInfoAsync(sessionId);
                return Results.Ok(info);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Create new session (force fresh)
        group.MapPost("/session/new", async (IAgentService agentService) =>
        {
            try
            {
                var sessionId = await agentService.CreateNewSessionAsync(SHARED_SLUG);
                var info = await agentService.GetSessionInfoAsync(sessionId);
                return Results.Ok(info);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Stream a chat message (SSE)
        group.MapPost("/", async (HttpContext ctx, [FromBody] KnowledgeChatRequest req, IAgentService agentService) =>
        {
            var sessionId = req.SessionId;
            if (string.IsNullOrEmpty(sessionId))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "SessionId required" }, ctx.RequestAborted);
                return;
            }

            var message = req.Message;

            var contextHint = $"[Context: Shared Knowledge Base. Use `ls`, `read`, `write` to manage entries. Categories are directories under the working directory. Entries are markdown files.]\n\n";

            ctx.Response.Headers["Content-Type"] = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"] = "keep-alive";

            await foreach (var evt in agentService.StreamPromptAsync(sessionId, contextHint + message, ctx.RequestAborted))
            {
                await ctx.Response.WriteAsync($"data: {evt}\n\n");
                await ctx.Response.Body.FlushAsync();
            }
        });

        // Abort
        group.MapPost("/abort", async ([FromBody] KnowledgeAbortRequest req, IAgentService agentService) =>
        {
            if (string.IsNullOrEmpty(req.SessionId))
                return Results.BadRequest(new { error = "SessionId required" });

            try
            {
                var result = await agentService.AbortSessionAsync(req.SessionId);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        // Kill session
        group.MapDelete("/session/{sessionId}", async (string sessionId, IAgentService agentService) =>
        {
            try
            {
                await agentService.KillSessionAsync(sessionId);
                return Results.Ok(new { killed = true });
            }
            catch
            {
                return Results.Ok(new { killed = false });
            }
        });
    }
}

public record KnowledgeChatRequest(string Message, string? SessionId);
public record KnowledgeAbortRequest(string SessionId);
