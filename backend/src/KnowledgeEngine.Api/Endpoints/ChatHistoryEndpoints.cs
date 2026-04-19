using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class ChatHistoryEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/chat");

        // Get chat history for a book
        group.MapGet("/", async (string slug, int? limit, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null)
                return Results.NotFound(new { error = "Book not found" });

            var messages = await db.ChatMessages
                .Where(m => m.BookId == book.Id)
                .OrderByDescending(m => m.CreatedAt)
                .Take(limit ?? 100)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Id, m.Role, m.Content, m.Thinking, m.CreatedAt })
                .ToListAsync();

            return Results.Ok(messages);
        });

        // Save a chat message
        group.MapPost("/", async (string slug, SaveChatMessageRequest req, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null)
                return Results.NotFound(new { error = "Book not found" });

            var msg = new ChatMessage
            {
                BookId = book.Id,
                Role = req.Role,
                Content = req.Content,
                Thinking = req.Thinking,
                CreatedAt = DateTime.UtcNow,
            };

            db.ChatMessages.Add(msg);
            await db.SaveChangesAsync();

            return Results.Ok(new { msg.Id, msg.Role, msg.Content, msg.Thinking, msg.CreatedAt });
        });

        // Clear chat history for a book
        group.MapDelete("/", async (string slug, AppDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null)
                return Results.NotFound(new { error = "Book not found" });

            db.ChatMessages.RemoveRange(db.ChatMessages.Where(m => m.BookId == book.Id));
            await db.SaveChangesAsync();

            return Results.Ok(new { cleared = true });
        });
    }
}

public record SaveChatMessageRequest(string Role, string Content, string? Thinking = null);
