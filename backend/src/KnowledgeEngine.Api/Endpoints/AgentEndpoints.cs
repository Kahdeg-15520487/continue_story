using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class AgentEndpoints
{
    public static void Map(WebApplication app)
    {
        // Ensure an agent session exists for a book (creates or restores from persistent storage)
        app.MapPost("/api/agent/session", async (
            AgentSessionRequest req,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.BookSlug) || req.BookSlug.Contains("..") || req.BookSlug.Contains('/') || req.BookSlug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            try
            {
                var sessionId = await agentService.EnsureSessionAsync(req.BookSlug, req.Mode ?? "read", ct);

                try
                {
                    var info = await agentService.GetSessionInfoAsync(sessionId, ct);
                    return Results.Ok(new
                    {
                        info.SessionId,
                        info.BookSlug,
                        info.Mode,
                        info.MessageCount,
                    });
                }
                catch
                {
                    return Results.Ok(new { sessionId, bookSlug = req.BookSlug, mode = req.Mode ?? "read", messageCount = 0 });
                }
            }
            catch
            {
                return Results.StatusCode(502);
            }
        });

        // Get active tasks for a book
        app.MapGet("/api/agent/tasks/{slug}", async (
            string slug,
            AgentTaskService taskService,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug, ct);
            if (book is null)
                return Results.NotFound(new { error = "Book not found" });

            var tasks = await taskService.GetActiveTasksAsync(book.Id);
            return Results.Ok(tasks.Select(t => new
            {
                t.Id,
                t.TaskType,
                t.Description,
                t.Status,
                t.ErrorMessage,
                t.UpdatedAt,
            }));
        });
    }
}

public record AgentSessionRequest(string BookSlug, string? Mode = "read");
