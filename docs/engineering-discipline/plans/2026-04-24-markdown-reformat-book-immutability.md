# Markdown Reformat & book.md Immutability Implementation Plan

> **Worker note:** Execute this plan task-by-task. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Add a markdown reformat phase after chapter splitting to fix broken formatting (line endings, speech lines, etc.), and make `book.md` immutable so the agent cannot modify or delete the original source.

**Architecture:** Add a `ReformatChaptersAsync` step to `ChapterSplitService` that runs after splitting and before title generation. It sends each chapter to the LLM with specific reformatting instructions using a fresh session per chapter (avoids compaction issues). For immutability, make `book.md` read-only on disk after splitting and add a guard in the agent's system prompt.

**Tech Stack:** C# / .NET 8, Pi Coding Agent SDK (LLM sessions), Docker volumes

**Work Scope:**
- **In scope:**
  - Markdown reformat phase in the chapter splitting pipeline
  - Making `book.md` read-only on disk after splitting
  - Adding immutability warning to agent system prompt
- **Out of scope:**
  - Changing the conversion pipeline (epub → md)
  - Changing how the frontend displays chapters
  - Reformatting wiki files
  - Any changes to the inline edit flow (scratch files)

**Verification Strategy:**
- **Level:** build-only
- **Command:** `cd backend && dotnet build KnowledgeEngine.sln && cd ../frontend && npx vite build`
- **What it validates:** All code compiles, no type errors or missing references

---

## File Structure Mapping

| File | Action | Responsibility |
|------|--------|---------------|
| `backend/src/KnowledgeEngine.Api/Services/ChapterSplitService.cs` | Modify | Add `ReformatChaptersAsync` method, call in pipeline, set book.md read-only |
| `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs` | Modify | Add immutability warning to system prompt |
| `backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs` | Modify | Mark book.md as read-only source in prompt |

---

### Task 1: Add Markdown Reformat Phase and book.md Immutability to ChapterSplitService

**Dependencies:** None
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/ChapterSplitService.cs`

- [ ] **Step 1: Add `ReformatChaptersAsync` method**

Add this method to `ChapterSplitService` after `GenerateChapterTitlesAsync`. Each chapter gets its own fresh session to avoid compaction issues:

```csharp
public async Task ReformatChaptersAsync(string slug)
{
    using var scope = _scopeFactory.CreateScope();
    var agentService = scope.ServiceProvider.GetRequiredService<IAgentService>();

    var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
    var chaptersDir = Path.Combine(libraryPath, slug, "chapters");

    if (!Directory.Exists(chaptersDir))
    {
        _logger.LogWarning("No chapters directory for {Slug}", slug);
        return;
    }

    var chapterFiles = Directory.GetFiles(chaptersDir, "*.md")
        .Where(f => !f.EndsWith(".scratch.md"))
        .OrderBy(f => f)
        .ToArray();

    if (chapterFiles.Length == 0) return;

    _logger.LogInformation("Reformatting {Count} chapters for {Slug}", chapterFiles.Length, slug);

    var systemPrompt = new StringBuilder()
        .AppendLine("You are a markdown formatting assistant. You receive a chapter of a story.")
        .AppendLine("Reformat it to clean markdown following these rules:")
        .AppendLine()
        .AppendLine("1. **Paragraphs**: Separate paragraphs with exactly one blank line. Merge hard-wrapped lines that belong to the same paragraph into a single paragraph.")
        .AppendLine("2. **Dialogue**: Each character's speech gets its own paragraph, wrapped in quotation marks. If dialogue is split across multiple lines, merge it into one line.")
        .AppendLine("   Example: `\"Hello,\" she said.` stays as one paragraph.")
        .AppendLine("3. **Scene breaks**: Use `---` (thematic break) for scene transitions. Replace `***` or blank lines with stars with `---`.")
        .AppendLine("4. **Emphasis**: Preserve existing `*italic*` and `**bold**`. Fix broken emphasis markers.")
        .AppendLine("5. **Metadata headers**: Remove any book metadata (Title:, Author:, Tags:, Description:, Novel ID:, etc.) from the beginning of chapters. The story text starts after the `# Chapter Title` heading.")
        .AppendLine("6. **No content changes**: Do NOT rewrite, summarize, or change any story text. Only fix formatting.")
        .AppendLine("7. **Output**: Return ONLY the reformatted chapter text. No explanation, no markdown code fences.")
        .ToString();

    foreach (var chapterFile in chapterFiles)
    {
        var chapterContent = await File.ReadAllTextAsync(chapterFile);
        var chapterName = Path.GetFileName(chapterFile);

        // Fresh session per chapter to avoid compaction issues
        var sessionId = await agentService.CreateNewSessionAsync(slug);

        try
        {
            await agentService.SendPromptAsync(sessionId, systemPrompt);
            var reformatPrompt = $"Here is the chapter to reformat:\n\n{chapterContent}";
            var result = await agentService.SendPromptAsync(sessionId, reformatPrompt);

            if (!string.IsNullOrWhiteSpace(result))
            {
                // Strip markdown code fences if the LLM wrapped the output
                var cleaned = result.Trim();
                if (cleaned.StartsWith("```markdown"))
                    cleaned = cleaned["```markdown".Length..];
                else if (cleaned.StartsWith("```md"))
                    cleaned = cleaned["```md".Length..];
                else if (cleaned.StartsWith("```"))
                    cleaned = cleaned[3..];
                if (cleaned.EndsWith("```"))
                    cleaned = cleaned[..^3];
                cleaned = cleaned.Trim();

                await File.WriteAllTextAsync(chapterFile, cleaned);
                _logger.LogInformation("Reformatted chapter {File}", chapterName);
            }
            else
            {
                _logger.LogWarning("Reformat returned empty for {File}, keeping original", chapterName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reformat failed for {File}", chapterName);
        }
        finally
        {
            try { await agentService.KillSessionAsync(sessionId); } catch { }
        }
    }

    _logger.LogInformation("Chapter reformatting complete for {Slug}", slug);
}
```

- [ ] **Step 2: Insert `ReformatChaptersAsync` into the pipeline and set book.md read-only**

In `SplitIntoChaptersAsync`, find this code near the end:

```csharp
            // Step 5: Generate chapter titles via LLM
            await GenerateChapterTitlesAsync(slug);

            // Step 6: Enqueue lore generation
            var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
            _logger.LogInformation("Lore generation enqueued for {Slug}", slug);
```

Replace with:

```csharp
            // Step 5: Reformat chapters to clean markdown
            await ReformatChaptersAsync(slug);

            // Step 6: Generate chapter titles via LLM
            await GenerateChapterTitlesAsync(slug);

            // Step 7: Mark book.md as read-only (immutable source)
            if (File.Exists(bookMd))
            {
                var attr = File.GetAttributes(bookMd);
                File.SetAttributes(bookMd, attr | FileAttributes.ReadOnly);
                _logger.LogInformation("Marked book.md as read-only for {Slug}", slug);
            }

            // Step 8: Enqueue lore generation
            var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
            jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
            _logger.LogInformation("Lore generation enqueued for {Slug}", slug);
```

- [ ] **Step 3: Build backend**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

---

### Task 2: Add book.md Immutability Warning to Agent System Prompt

**Dependencies:** None (can run in parallel with Task 1)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs`

- [ ] **Step 1: Add immutability rule to system prompt**

In `ChatEndpoints.cs`, in the context prompt `StringBuilder`, find this line in the Guidelines section:

```csharp
.AppendLine("- Do NOT modify files unless the user explicitly asks you to")
```

Add immediately after it:

```csharp
.AppendLine("- NEVER modify or delete `book.md` — it is the immutable original source. Only work with `chapters/` and `wiki/` directories")
```

- [ ] **Step 2: Update LoreJobService prompt**

In `LoreJobService.cs`, find:

```csharp
var prompt = $"Read the book at book.md and extract lore using the lore-extraction skill. "
```

Replace with:

```csharp
var prompt = $"Read the book at book.md (read-only original source) and extract lore using the lore-extraction skill. "
```

- [ ] **Step 3: Build backend**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

---

### Task 3 (Final): End-to-End Build Verification

**Dependencies:** Task 1, Task 2
**Files:** None (read-only verification)

- [ ] **Step 1: Build backend**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded, 0 errors

- [ ] **Step 2: Build frontend**

Run: `cd J:/workspace2/llm/continue_story_4/frontend && npx vite build`
Expected: Build completes with no errors

- [ ] **Step 3: Verify plan success criteria**

- [ ] `ReformatChaptersAsync` method exists and is called before `GenerateChapterTitlesAsync`
- [ ] book.md is set to read-only after splitting completes
- [ ] System prompt tells the agent to never modify `book.md`
- [ ] Reformatting uses a fresh session per chapter (avoids compaction)
- [ ] LoreJobService prompt marks book.md as read-only source

- [ ] **Step 4: Commit and deploy**

```bash
cd J:/workspace2/llm/continue_story_4
git add -A
git commit -m "feat: add markdown reformat phase and make book.md immutable"
git push
docker compose up -d --build
```
