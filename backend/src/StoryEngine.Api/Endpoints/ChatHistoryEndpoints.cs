using StoryEngine.Api.Data;
using StoryEngine.Api.Models;
using StoryEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace StoryEngine.Api.Endpoints;

public static class ChatHistoryEndpoints
{
    private const string SHARED_SLUG = "__shared__";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/chat");

        // Get chat history
        group.MapGet("/", async (string slug, int? limit, HttpContext context, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            IQueryable<ChatMessage> query;

            if (slug == SHARED_SLUG)
            {
                query = db.ChatMessages.Where(m => m.BookId == null);
            }
            else
            {
                var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
                if (book is null)
                    return Results.NotFound(new { error = "Book not found" });
                query = db.ChatMessages.Where(m => m.BookId == book.Id);
            }

            var sessionId = context.Request.Query.ContainsKey("sessionId")
                ? context.Request.Query["sessionId"].ToString()
                : null;
            if (!string.IsNullOrEmpty(sessionId))
                query = query.Where(m => m.SessionId == sessionId);

            var messages = await query
                .OrderByDescending(m => m.CreatedAt)
                .Take(limit ?? 100)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Id, m.Role, m.Content, m.SessionId, m.CreatedAt })
                .ToListAsync();

            return Results.Ok(messages);
        });

        // Save a chat message
        group.MapPost("/", async (string slug, SaveChatMessageRequest req, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            int? bookId = null;
            if (slug != SHARED_SLUG)
            {
                var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
                if (book is null)
                    return Results.NotFound(new { error = "Book not found" });
                bookId = book.Id;
            }

            var msg = new ChatMessage
            {
                BookId = bookId,
                Role = req.Role,
                Content = req.Content,
                Thinking = req.Thinking,
                SessionId = req.SessionId,
                CreatedAt = DateTime.UtcNow,
            };

            db.ChatMessages.Add(msg);
            await db.SaveChangesAsync();

            return Results.Ok(new { msg.Id, msg.Role, msg.Content, msg.Thinking, msg.CreatedAt });
        });

        // Clear chat history
        group.MapDelete("/", async (string slug, HttpContext context, AppDbContext db, IAgentService agentService) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var sessionId = context.Request.Query.ContainsKey("sessionId")
                ? context.Request.Query["sessionId"].ToString()
                : null;
            if (!string.IsNullOrEmpty(sessionId))
            {
                try { await agentService.KillSessionAsync(sessionId); } catch { }
            }

            IQueryable<ChatMessage> query;
            if (slug == SHARED_SLUG)
            {
                query = db.ChatMessages.Where(m => m.BookId == null);
            }
            else
            {
                var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
                if (book is null)
                    return Results.NotFound(new { error = "Book not found" });
                query = db.ChatMessages.Where(m => m.BookId == book.Id);
            }

            if (!string.IsNullOrEmpty(sessionId))
                query = query.Where(m => m.SessionId == sessionId);

            db.ChatMessages.RemoveRange(await query.ToListAsync());
            await db.SaveChangesAsync();

            return Results.Ok(new { cleared = true });
        });
    }
}

public record SaveChatMessageRequest(string Role, string Content, string? Thinking = null, string? SessionId = null);
