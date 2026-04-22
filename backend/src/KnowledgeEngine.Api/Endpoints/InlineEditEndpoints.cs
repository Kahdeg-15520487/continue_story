using KnowledgeEngine.Api.Services;
using System.Text.Json;

namespace KnowledgeEngine.Api.Endpoints;

public static class InlineEditEndpoints
{
    public record InlineEditRequest(string SelectedText, string Instruction);

    public static void Map(WebApplication app)
    {
        // Endpoint A: POST /api/books/{slug}/chapters/{id}/inline-edit — SSE streaming
        app.MapPost("/api/books/{slug}/chapters/{id}/inline-edit", async (
            string slug,
            string id,
            InlineEditRequest req,
            IAgentService agentService,
            IConfiguration config,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid book slug" }, ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(req.SelectedText))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Selected text must not be empty" }, ct);
                return;
            }

            if (string.IsNullOrWhiteSpace(req.Instruction))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Instruction must not be empty" }, ct);
                return;
            }

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var chapterFile = Path.Combine(libraryPath, slug, "chapters", $"{id}.md");

            if (!File.Exists(chapterFile))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "Chapter file not found" }, ct);
                return;
            }

            var chapterContent = await File.ReadAllTextAsync(chapterFile, ct);

            if (!chapterContent.Contains(req.SelectedText, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Selected text not found in chapter content" }, ct);
                return;
            }

            var chapterTitle = chapterContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? id;

            var selectionStartLine = 1;
            var selectionEndLine = 1;
            var selectionIndex = chapterContent.IndexOf(req.SelectedText, StringComparison.Ordinal);
            for (int i = 0; i < selectionIndex; i++)
            {
                if (chapterContent[i] == '\n') selectionStartLine++;
            }
            var afterSelectionStart = selectionIndex + req.SelectedText.Length;
            for (int i = selectionIndex; i < afterSelectionStart && i < chapterContent.Length; i++)
            {
                if (chapterContent[i] == '\n') selectionEndLine++;
            }

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are editing a chapter of a book. The user has selected some text and given an instruction.");
            sb.AppendLine();
            sb.AppendLine($"## Chapter: {chapterTitle}");
            sb.AppendLine(chapterContent);
            sb.AppendLine();
            sb.AppendLine($"## Selected Text (lines {selectionStartLine}-{selectionEndLine})");
            sb.AppendLine("```");
            sb.AppendLine(req.SelectedText);
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Instruction");
            sb.AppendLine(req.Instruction);
            sb.AppendLine();
            sb.AppendLine("Rewrite the ENTIRE chapter incorporating the requested change. Keep everything else the same unless the instruction requires broader changes. Output the complete rewritten chapter as markdown.");
            sb.AppendLine();
            sb.AppendLine($"Write the result to: chapters/{id}.scratch.md");
            var prompt = sb.ToString();

            var response = ctx.Response;
            response.ContentType = "text/event-stream";
            response.Headers.Append("Cache-Control", "no-cache");
            response.Headers.Append("Connection", "keep-alive");

            var sessionId = await agentService.EnsureSessionAsync(slug, ct);

            try
            {
                await foreach (var evt in agentService.StreamPromptAsync(sessionId, prompt, ct))
                {
                    await response.WriteAsync($"data: {evt}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }

                var scratchFile = Path.Combine(libraryPath, slug, "chapters", $"{id}.scratch.md");
                var scratchExists = File.Exists(scratchFile);

                var doneEvent = JsonSerializer.Serialize(new
                {
                    type = "edit_done",
                    scratchPath = $"chapters/{id}.scratch.md",
                    exists = scratchExists
                });
                await response.WriteAsync($"data: {doneEvent}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            finally
            {
                try
                {
                    await agentService.KillSessionAsync(sessionId, ct);
                }
                catch { }
            }
        });

        // Endpoint B: POST /api/books/{slug}/chapters/{id}/inline-edit/accept
        app.MapPost("/api/books/{slug}/chapters/{id}/inline-edit/accept", async (
            string slug,
            string id,
            IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var scratchFile = Path.Combine(libraryPath, slug, "chapters", $"{id}.scratch.md");

            if (!File.Exists(scratchFile))
                return Results.NotFound(new { error = "Scratch file not found" });

            var scratchContent = await File.ReadAllTextAsync(scratchFile);
            var chapterFile = Path.Combine(libraryPath, slug, "chapters", $"{id}.md");
            await File.WriteAllTextAsync(chapterFile, scratchContent);
            File.Delete(scratchFile);

            return Results.Ok(new { accepted = true });
        });

        // Endpoint C: POST /api/books/{slug}/chapters/{id}/inline-edit/reject
        app.MapPost("/api/books/{slug}/chapters/{id}/inline-edit/reject", (
            string slug,
            string id,
            IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var scratchFile = Path.Combine(libraryPath, slug, "chapters", $"{id}.scratch.md");

            if (File.Exists(scratchFile))
                File.Delete(scratchFile);

            return Results.Ok(new { rejected = true });
        });

        // Endpoint D: GET /api/books/{slug}/chapters/{id}/scratch
        app.MapGet("/api/books/{slug}/chapters/{id}/scratch", (
            string slug,
            string id,
            IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var scratchFile = Path.Combine(libraryPath, slug, "chapters", $"{id}.scratch.md");

            if (!File.Exists(scratchFile))
                return Results.NotFound(new { error = "Scratch file not found" });

            var content = File.ReadAllText(scratchFile);
            return Results.Ok(new { content });
        });
    }
}
