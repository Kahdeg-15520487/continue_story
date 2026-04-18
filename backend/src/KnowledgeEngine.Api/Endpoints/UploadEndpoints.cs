using Hangfire;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class UploadEndpoints
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".docx", ".doc", ".txt", ".html", ".htm",
        ".pptx", ".xlsx", ".xls", ".csv", ".ipynb", ".md"
    };

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/upload");

        group.MapPost("/", async (
            string slug,
            IFormFile file,
            AppDbContext db,
            IConfiguration config,
            IBackgroundJobClient jobClient) =>
        {
            // Validate slug
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            // Validate book exists
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null)
                return Results.NotFound(new { error = "Book not found" });

            // Validate file was provided
            if (file.Length == 0)
                return Results.BadRequest(new { error = "File is empty" });

            // Validate extension
            var extension = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(extension))
                return Results.BadRequest(new
                {
                    error = $"Unsupported file type: {extension}. Allowed: {string.Join(", ", AllowedExtensions)}"
                });

            // Save file to library volume
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            Directory.CreateDirectory(bookDir);

            // Sanitize filename — keep only the filename, no path components
            var safeFileName = Path.GetFileName(file.FileName);
            var filePath = Path.Combine(bookDir, safeFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Update book metadata
            book.SourceFile = safeFileName;
            book.Status = "converting";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            // Trigger conversion via Hangfire
            var outputPath = Path.Combine(bookDir, "book.md");
            var jobId = jobClient.Enqueue<IConversionService>(x =>
                x.ConvertToMarkdownAsync(filePath, outputPath, CancellationToken.None));

            // Update book status when conversion completes
            // Use the parent jobId to prevent duplicate continuations
            jobClient.ContinueJobWith<ConversionJobService>(jobId,
                service => service.UpdateBookAfterConversion(book.Id));

            // Store the conversion job ID so we can cancel retries on re-upload
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;

            return Results.Ok(new
            {
                book.Slug,
                sourceFile = safeFileName,
                size = file.Length,
                status = "converting",
                jobId
            });
        })
        .DisableAntiforgery()     // No CSRF for API-only project
        .WithMetadata(new RequestSizeLimitAttribute(100 * 1024 * 1024));  // 100MB limit
    }
}
