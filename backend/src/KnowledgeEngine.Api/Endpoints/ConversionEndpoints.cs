using Hangfire;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class ConversionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/convert");

        // Trigger conversion (enqueues Hangfire job)
        group.MapPost("/", async (
            string slug,
            AppDbContext db,
            IConfiguration config,
            IBackgroundJobClient jobClient) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null) return Results.NotFound(new { error = "Book not found" });

            if (string.IsNullOrEmpty(book.SourceFile))
                return Results.BadRequest(new { error = "No source file set for this book" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            var inputPath = Path.Combine(bookDir, book.SourceFile);
            var outputPath = Path.Combine(bookDir, "book.md");

            book.Status = "converting";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var jobId = jobClient.Enqueue<IConversionService>(x =>
                x.ConvertToMarkdownAsync(inputPath, outputPath, CancellationToken.None));

            // Update book status when job completes via continuation
            jobClient.ContinueJobWith<ConversionJobService>(
                jobId,
                service => service.UpdateBookAfterConversion(book.Id));

            return Results.Accepted(null, new { jobId, status = "queued" });
        });

        // Retry chapter splitting (re-queues after timeout/error, resumes from last chapter)
        group.MapPost("/split", async (
            string slug,
            AppDbContext db,
            IConfiguration config,
            IBackgroundJobClient jobClient) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null) return Results.NotFound(new { error = "Book not found" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookMd = Path.Combine(libraryPath, slug, "book.md");

            if (!File.Exists(bookMd))
                return Results.BadRequest(new { error = "No book.md found" });

            book.Status = "splitting";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var jobId = jobClient.Enqueue<ChapterSplitService>(x => x.SplitIntoChaptersAsync(slug));
            return Results.Ok(new { jobId, status = "queued" });
        });
    }
}
