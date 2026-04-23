using Hangfire;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;

namespace KnowledgeEngine.Api.Endpoints;

public static class ChapterEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/chapters");

        // List chapters
        group.MapGet("/", async (string slug, ChapterService chapterService) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            var chapters = await chapterService.ListChaptersAsync(slug);
            return Results.Ok(chapters);
        });

        // Get chapter content
        group.MapGet("/{id}", async (string slug, string id, ChapterService chapterService) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            var chapter = await chapterService.GetChapterAsync(slug, id);
            return chapter is null ? Results.NotFound(new { error = "Chapter not found" }) : Results.Ok(chapter);
        });

        // Save chapter content
        group.MapPut("/{id}", async (string slug, string id, UpdateChapterRequest req, ChapterService chapterService) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            try
            {
                await chapterService.SaveChapterAsync(slug, id, req.Content);
                return Results.Ok(new { saved = true });
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Insert new chapter
        group.MapPost("/", async (string slug, InsertChapterRequest req, ChapterService chapterService) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            var chapter = await chapterService.InsertChapterAsync(slug, req.Title, req.AfterChapterId);
            return Results.Ok(chapter);
        });

        // Delete chapter
        group.MapDelete("/{id}", async (string slug, string id, ChapterService chapterService) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            try
            {
                await chapterService.DeleteChapterAsync(slug, id);
                return Results.Ok(new { deleted = true });
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Reorder chapters
        group.MapPost("/reorder", async (string slug, ReorderChaptersRequest req, ChapterService chapterService) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            if (req.OrderedIds == null || req.OrderedIds.Length == 0)
                return Results.BadRequest(new { error = "Ordered IDs required" });

            try
            {
                await chapterService.ReorderChaptersAsync(slug, req.OrderedIds);
                return Results.Ok(new { reordered = true });
            }
            catch (FileNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });

        // Regenerate chapter titles
        group.MapPost("/regenerate-titles", (string slug, IBackgroundJobClient jobClient) =>
        {
            if (InvalidSlug(slug)) return Results.BadRequest(new { error = "Invalid slug" });
            var jobId = jobClient.Enqueue<ChapterSplitService>(x => x.GenerateChapterTitlesAsync(slug));
            return Results.Ok(new { queued = true, jobId });
        });
    }

    private static bool InvalidSlug(string slug) =>
        string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\');
}

public record UpdateChapterRequest(string Content);
public record InsertChapterRequest(string Title, string? AfterChapterId);
public record ReorderChaptersRequest(string[] OrderedIds);
