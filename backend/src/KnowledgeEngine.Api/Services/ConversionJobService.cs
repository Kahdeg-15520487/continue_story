using System.Text;
using Hangfire;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Services;

public class ConversionJobService
{
    private readonly ILogger<ConversionJobService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public ConversionJobService(
        ILogger<ConversionJobService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    // Hangfire continuation job — runs after conversion completes
    public async Task UpdateBookAfterConversion(int bookId)
    {
        // Create a new scope for Hangfire background execution
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation("Updating book status after conversion: BookId={BookId}", bookId);

        var book = await db.Books.FindAsync(bookId);
        if (book is null)
        {
            _logger.LogWarning("Book not found: BookId={BookId}", bookId);
            return;
        }

        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookDir = Path.Combine(libraryPath, book.Slug);
        var outputPath = Path.Combine(bookDir, "book.md");

        if (File.Exists(outputPath))
        {
            var info = new FileInfo(outputPath);
            if (info.Length > 0)
            {
                book.Status = "ready";
                book.UpdatedAt = DateTime.UtcNow;
                _logger.LogInformation("Book marked as ready: Slug={Slug} ({Size} bytes)", book.Slug, info.Length);

                var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();

                // Generate AI title before splitting
                try
                {
                    await GenerateBookTitleAsync(book, outputPath, scope);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI title generation failed for {Slug}, keeping filename-derived title", book.Slug);
                }

                jobClient.Enqueue<ChapterSplitService>(x => x.SplitIntoChaptersAsync(book.Slug));
                _logger.LogInformation("Chapter splitting auto-triggered for {Slug}", book.Slug);
            }
            else
            {
                // Empty output file — conversion produced no content
                book.Status = "error";
                book.ErrorMessage = "Conversion produced empty output";
                book.UpdatedAt = DateTime.UtcNow;
                _logger.LogError("Book marked as error (empty output): Slug={Slug}", book.Slug);
            }
        }
        else
        {
            book.Status = "error";
            book.ErrorMessage = "Output file was not created";
            book.UpdatedAt = DateTime.UtcNow;
            _logger.LogError("Book marked as error (output file not found): Slug={Slug}", book.Slug);
        }

        await db.SaveChangesAsync();
    }

    private async Task GenerateBookTitleAsync(Book book, string bookMdPath, IServiceScope scope)
    {
        var agentService = scope.ServiceProvider.GetRequiredService<IAgentService>();

        // Read first ~1500 chars of content
        var content = await File.ReadAllTextAsync(bookMdPath);
        var opening = content.Length > 1500 ? content[..1500] : content;

        var filename = book.SourceFile ?? "";

        var sb = new StringBuilder();
        sb.AppendLine($"Based on the filename \"{filename}\" and the opening text below, suggest a concise, compelling book title.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Return ONLY the title text, nothing else — no quotes, no explanation, no formatting");
        sb.AppendLine("- The title should be 1-8 words");
        sb.AppendLine("- If the filename already contains a good title, use it or refine it");
        sb.AppendLine("- Prefer the title that best represents the story content");
        sb.AppendLine();
        sb.AppendLine("Opening text:");
        sb.AppendLine(opening);
        var prompt = sb.ToString();

        var sessionId = await agentService.CreateNewSessionAsync(book.Slug);
        try
        {
            _logger.LogInformation("Generating AI title for {Slug}", book.Slug);
            var response = await agentService.SendPromptAsync(sessionId, prompt);
            var title = response.Trim().Trim('"', '\'', '`');

            if (!string.IsNullOrWhiteSpace(title) && title.Length <= 200)
            {
                book.Title = title;
                book.UpdatedAt = DateTime.UtcNow;
                _logger.LogInformation("AI title for {Slug}: {Title}", book.Slug, title);
            }
        }
        finally
        {
            try { await agentService.KillSessionAsync(sessionId); } catch { }
        }
    }
}
