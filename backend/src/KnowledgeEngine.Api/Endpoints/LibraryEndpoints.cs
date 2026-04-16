using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class LibraryEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books");

        group.MapGet("/", async (AppDbContext db) =>
        {
            var books = await db.Books
                .OrderBy(b => b.Title)
                .Select(b => new BookSummaryDto(b))
                .ToListAsync();
            return Results.Ok(books);
        });

        group.MapGet("/{slug}", async (string slug, AppDbContext db) =>
        {
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            return book is null ? Results.NotFound() : Results.Ok(new BookDetailDto(book));
        });

        group.MapPost("/", async (CreateBookRequest req, AppDbContext db, IConfiguration config) =>
        {
            var slug = GenerateSlug(req.Title);
            if (await db.Books.AnyAsync(b => b.Slug == slug))
                return Results.Conflict(new { error = "Book already exists" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            Directory.CreateDirectory(bookDir);

            var book = new Book
            {
                Slug = slug,
                Title = req.Title,
                Author = req.Author,
                Year = req.Year,
                SourceFile = req.SourceFile,
                Status = "pending",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Books.Add(book);
            await db.SaveChangesAsync();

            return Results.Created($"/api/books/{slug}", new BookDetailDto(book));
        });

        group.MapDelete("/{slug}", async (string slug, AppDbContext db, IConfiguration config) =>
        {
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null) return Results.NotFound();

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            if (Directory.Exists(bookDir))
                Directory.Delete(bookDir, recursive: true);

            db.Books.Remove(book);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });
    }

    private static string GenerateSlug(string title)
    {
        var parts = title
            .ToLowerInvariant()
            .Split(' ', '_', '/', '\\')
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9]", ""))
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        return string.Join('-', parts);
    }
}

// DTOs
public record BookSummaryDto(int Id, string Slug, string Title, string? Author, int? Year, string Status, DateTime UpdatedAt)
{
    public BookSummaryDto(Book b) : this(b.Id, b.Slug, b.Title, b.Author, b.Year, b.Status, b.UpdatedAt) { }
}

public record BookDetailDto(int Id, string Slug, string Title, string? Author, int? Year, string? SourceFile, string Status, string? ErrorMessage, DateTime CreatedAt, DateTime UpdatedAt)
{
    public BookDetailDto(Book b) : this(b.Id, b.Slug, b.Title, b.Author, b.Year, b.SourceFile, b.Status, b.ErrorMessage, b.CreatedAt, b.UpdatedAt) { }
}

public record CreateBookRequest(string Title, string? Author, int? Year, string? SourceFile);
