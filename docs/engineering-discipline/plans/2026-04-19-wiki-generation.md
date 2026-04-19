# Wiki Generation — End-to-End Fix

> **Worker note:** Execute this plan task-by-task. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Make wiki/lore generation actually work end-to-end. Currently the pipeline is broken: the `wiki/` directory isn't pre-created, there's no verification that wiki files were written, no book status update after generation, no auto-trigger after conversion, and the skill prompt needs better structure for reliable extraction.

**Architecture:** The current flow is: user clicks "Generate Lore" → Hangfire enqueues `LoreJobService.GenerateLoreAsync(slug)` → creates agent session → sends prompt → agent writes wiki files via coding tools. The fix ensures every step works reliably and adds auto-trigger after book conversion.

**Tech Stack:**
- **Backend (.NET):** Fix `LoreJobService`, add auto-trigger in `ConversionJobService`, add status tracking
- **Agent bridge (Node.js):** Remove broken `skillsOverride` filter that blocks coding tools
- **Skill prompt:** Improve `skills/lore-extraction/SKILL.md` for better structured output
- **No new dependencies or containers**

**Work Scope:**
- **In scope:** Fix lore pipeline, auto-trigger, verification, status updates, skill prompt improvement
- **Out of scope:** Frontend wiki UI changes (already functional), new wiki file types, incremental wiki updates

---

**Verification Strategy:**
- **Level:** build + docker integration test
- **Command:** `docker compose build && docker compose up -d` → upload book → verify auto-trigger → verify wiki files created → verify book status

---

## File Structure Mapping

```
backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs       # MODIFY — pre-create wiki dir, verify output, update status
backend/src/KnowledgeEngine.Api/Services/ConversionJobService.cs  # MODIFY — auto-trigger lore after successful conversion
agent/src/index.ts                                                # MODIFY — remove skillsOverride filter
skills/lore-extraction/SKILL.md                                   # MODIFY — improve extraction prompt
```

---

## Task 1: Fix Agent Bridge — Remove Broken skillsOverride

**Dependencies:** None
**Files:**
- Modify: `agent/src/index.ts`

The current `skillsOverride` in `createSession` filters skills to only `"lore-extraction"`. This is wrong for two reasons:
1. The skill is a prompt guide, not a tool — filtering by skill name doesn't restrict tools
2. The agent needs coding tools (file write) to create wiki files — the skill alone doesn't provide that
3. For chat sessions, the agent should have NO skills (just answer questions from context)

### Step 1: Edit `agent/src/index.ts`

Find the `skillsOverride` block:

```typescript
    skillsOverride: (current) => ({
      skills: current.skills.filter(s =>
        s.name === "lore-extraction"
      ),
      diagnostics: current.diagnostics,
    }),
```

Replace with:

```typescript
    skillsOverride: mode === "write"
      ? (current) => ({ skills: current.skills, diagnostics: current.diagnostics })
      : (current) => ({ skills: [], diagnostics: current.diagnostics }),
```

This means:
- **Write mode** (lore generation): all skills available + coding tools → agent can read book, follow skill instructions, write wiki files
- **Read mode** (chat): no skills + read-only tools → agent just answers from context, no file access

### Step 2: Build agent container

```bash
docker compose build agent
```

### Step 3: Commit

```bash
git add agent/src/index.ts
git commit -m "fix(agent): remove restrictive skillsOverride, allow skills for write mode, disable for read mode"
```

---

## Task 2: Improve Lore Extraction Skill Prompt

**Dependencies:** None
**Files:**
- Modify: `skills/lore-extraction/SKILL.md`

The current skill prompt is decent but needs: explicit instructions to create the wiki directory first, clearer file format expectations, and handling for edge cases (very short books, already-existing wiki files).

### Step 1: Replace `skills/lore-extraction/SKILL.md`

Replace the ENTIRE file with:

```markdown
---
name: lore-extraction
description: Extracts structured lore data (characters, locations, themes, plot summary) from markdown book files. Use when asked to analyze a book's content and generate wiki-style reference pages.
---

# Lore Extraction Skill

You are a literary analysis assistant. Your task is to read a book's markdown file and produce structured wiki pages.

## Workflow

1. **Read the book file** at the path given in the prompt (e.g., `book.md`)
2. **Create the `wiki/` directory** if it does not already exist
3. **Analyze the content** and extract:
   - Characters: name, role, description, relationships
   - Locations: name, description, plot significance
   - Themes: major themes with evidence from the text
   - Summary: concise plot summary covering beginning, middle, and end
4. **Write four wiki files** to the `wiki/` directory:
   - `wiki/characters.md`
   - `wiki/locations.md`
   - `wiki/themes.md`
   - `wiki/summary.md`
5. **Overwrite** any existing wiki files with fresh analysis

## Output Formats

### wiki/characters.md

```markdown
# Characters

> Auto-generated lore extraction

## [Character Name]

**Role:** Protagonist / Antagonist / Supporting / Minor

**Description:** 2-4 sentence character description covering personality, motivations, and arc.

**Relationships:**
- Related to [Other Character] — [relationship type]
```

### wiki/locations.md

```markdown
# Locations

> Auto-generated lore extraction

## [Location Name]

**Description:** 2-3 sentence description of the location.

**Significance:** Why this location matters to the plot.
```

### wiki/themes.md

```markdown
# Themes

> Auto-generated lore extraction

## [Theme Name]

**Description:** What this theme explores.

**Evidence:**
- [Specific example from the text]
- [Another example]
```

### wiki/summary.md

```markdown
# Plot Summary

> Auto-generated lore extraction

## Overview

[1-2 paragraph high-level summary of the entire book]

## Act I — [Title or "Beginning"]

[Summary of the opening setup]

## Act II — [Title or "Middle"]

[Summary of the rising action and conflicts]

## Act III — [Title or "Ending"]

[Summary of the resolution]
```

## Rules

- Only extract information **explicitly present** in the text — do not invent or infer beyond what is written
- For very long books, prioritize main characters and pivotal locations
- For very short books (under 500 words), combine characters into a single list with brief entries
- Use proper markdown formatting throughout
- Each file MUST start with a top-level heading (`# Title`)
- If the book has no identifiable characters or locations (e.g., a technical document), write "No [characters/locations] identified — this appears to be a non-fiction or technical work" in the relevant file
```

### Step 2: Commit

```bash
git add skills/lore-extraction/SKILL.md
git commit -m "docs(skill): improve lore-extraction prompt with explicit directory creation, clearer formats, edge case handling"
```

---

## Task 3: Fix LoreJobService — Pre-create Wiki Dir, Verify Output, Update Status

**Dependencies:** None
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs`

### Step 1: Replace `LoreJobService.cs`

Replace the ENTIRE file with:

```csharp
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Services;

public class LoreJobService
{
    private readonly ILogger<LoreJobService> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public LoreJobService(
        ILogger<LoreJobService> logger,
        IConfiguration config,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    public async Task GenerateLoreAsync(string slug)
    {
        using var scope = _scopeFactory.CreateScope();
        var agentService = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var db = scope.ServiceProvider.GetRequiredService<KnowledgeEngine.Api.Data.AppDbContext>();

        _logger.LogInformation("Generating lore for book: Slug={Slug}", slug);

        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookMd = Path.Combine(libraryPath, slug, "book.md");

        var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
        if (book is null)
        {
            _logger.LogError("Book not found in DB: {Slug}", slug);
            return;
        }

        if (!File.Exists(bookMd) || new FileInfo(bookMd).Length == 0)
        {
            book.Status = "error";
            book.ErrorMessage = "Cannot generate lore: book has no content";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogError("Lore generation skipped: no book.md for {Slug}", slug);
            return;
        }

        // Pre-create wiki directory
        var wikiDir = Path.Combine(libraryPath, slug, "wiki");
        Directory.CreateDirectory(wikiDir);

        // Update status to generating
        book.Status = "generating-lore";
        book.ErrorMessage = null;
        book.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var prompt = $"Read the book at book.md and extract lore using the lore-extraction skill. " +
            $"Generate wiki files in the wiki/ directory: characters.md, locations.md, themes.md, and summary.md. " +
            $"Follow the skill's output format exactly. The working directory is {Path.Combine(libraryPath, slug)}";

        try
        {
            var sessionId = await agentService.EnsureSessionAsync(slug, "write");
            await agentService.SendPromptAsync(sessionId, prompt);

            // Verify wiki files were actually created
            var expectedFiles = new[] { "characters.md", "locations.md", "themes.md", "summary.md" };
            var createdFiles = expectedFiles
                .Where(f => File.Exists(Path.Combine(wikiDir, f)))
                .ToList();

            if (createdFiles.Count == 0)
            {
                book.Status = "error";
                book.ErrorMessage = "Lore generation completed but no wiki files were created. The agent may not have followed instructions.";
                book.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                _logger.LogError("Lore generation produced no files for {Slug}", slug);
                return;
            }

            book.Status = "lore-ready";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogInformation("Lore generation complete for {Slug}: {Count} wiki files created ({Files})",
                slug, createdFiles.Count, string.Join(", ", createdFiles));
        }
        catch (Exception ex)
        {
            book.Status = "error";
            book.ErrorMessage = $"Lore generation failed: {ex.Message}";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            _logger.LogError(ex, "Lore generation failed for {Slug}", slug);
        }
    }
}
```

Key changes:
- **Pre-creates `wiki/` directory** with `Directory.CreateDirectory`
- **Updates book status** to `"generating-lore"` before starting
- **Verifies wiki files exist** after agent completes
- **Sets status to `"lore-ready"`** on success, `"error"` on failure
- **Always updates book status** — even on exception
- **Simpler prompt** — references the skill by name, lets the agent follow the skill's instructions

### Step 2: Build

```bash
cd backend && dotnet build KnowledgeEngine.sln
```

Expected: Build succeeds.

### Step 3: Commit

```bash
git add backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs
git commit -m "fix(lore): pre-create wiki dir, verify output files, track status (generating-lore → lore-ready/error)"
```

---

## Task 4: Auto-Trigger Lore After Conversion

**Dependencies:** None (can run in parallel with Tasks 1-3)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/ConversionJobService.cs`

### Step 1: Edit `ConversionJobService.cs`

After the book is marked as `"ready"`, auto-trigger lore generation. Find the block:

```csharp
            if (info.Length > 0)
            {
                book.Status = "ready";
                book.UpdatedAt = DateTime.UtcNow;
                _logger.LogInformation("Book marked as ready: Slug={Slug} ({Size} bytes)", book.Slug, info.Length);
            }
```

Replace with:

```csharp
            if (info.Length > 0)
            {
                book.Status = "ready";
                book.UpdatedAt = DateTime.UtcNow;
                _logger.LogInformation("Book marked as ready: Slug={Slug} ({Size} bytes)", book.Slug, info.Length);

                // Auto-trigger lore generation
                var jobClient = scope.ServiceProvider.GetRequiredService<IBackgroundJobClient>();
                jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(book.Slug));
                _logger.LogInformation("Lore generation auto-triggered for {Slug}", book.Slug);
            }
```

Also add the Hangfire using if not present. Check if the file has `using Hangfire;` — it does. Also add the LoreJobService using — check if it's there. The file currently has `using KnowledgeEngine.Api.Services;` — no, it doesn't. But `LoreJobService` is in the same namespace `KnowledgeEngine.Api.Services`. Actually let's check:

The file starts with:
```csharp
using Hangfire;
using KnowledgeEngine.Api.Data;
using Microsoft.EntityFrameworkCore;
```

`LoreJobService` is in namespace `KnowledgeEngine.Api.Services` — same as `ConversionJobService`. So no additional using needed. But `IBackgroundJobClient` comes from `Hangfire` — already imported.

### Step 2: Build

```bash
cd backend && dotnet build KnowledgeEngine.sln
```

Expected: Build succeeds.

### Step 3: Commit

```bash
git add backend/src/KnowledgeEngine.Api/Services/ConversionJobService.cs
git commit -m "feat(lore): auto-trigger lore generation after successful book conversion"
```

---

## Task 5: End-to-End Verification

**Dependencies:** Tasks 1–4
**Files:** None (read-only verification)

### Step 1: Build and start all containers

```bash
cd J:/workspace2/llm/continue_story_4
docker compose down -v 2>/dev/null || true
docker compose build 2>&1
docker compose up -d 2>&1
```

### Step 2: Wait for services

```bash
sleep 15
curl -f http://localhost:5000/api/health
```

### Step 3: Upload a book and verify auto-trigger

```bash
# Create a book with meaningful content for lore extraction
cat > /tmp/test-lore.txt << 'EOF'
The Dragon's Apprentice by Jane Author

Chapter 1: The Beginning

Elara was a young blacksmith's daughter living in the village of Thornhaven, nestled at the foot of the Crystal Mountains. She had always dreamed of adventure beyond the forge. One day, a wounded dragon named Thormund crash-landed in her father's workshop. Against her father's wishes, Elara nursed the dragon back to health.

Chapter 2: The Journey

Thormund revealed that the Crystal Mountains were home to the last dragon sanctuary, now under threat from the sorcerer Malachar. Elara and Thormund set out on a journey through the Whispering Woods, where they met Finn, a wandering bard who knew the ancient paths. Together, they faced shadow wolves and crossed the Bridge of Echoes.

Chapter 3: The Confrontation

At the summit of Crystal Peak, Malachar had erected a dark tower that siphoned the mountains' magic. Elara discovered she had the rare gift of dragon-speaking, allowing her to rally the remaining dragons. In the final battle, Finn's songs weakened Malachar's spells while Thormund led the dragon assault. Elara confronted Malachar directly, using her blacksmith knowledge to shatter his crystal focus.

Chapter 4: Aftermath

With Malachar defeated, the Crystal Mountains were restored. The dragons returned to their sanctuary. Elara became the first human guardian of the dragon sanctuary. Finn wrote a ballad about their adventure that spread across the kingdom. Thornhaven became a place of pilgrimage for those seeking to meet the dragons.
EOF

curl -s -X POST http://localhost:5000/api/books -H "Content-Type: application/json" -d '{"title":"The Dragons Apprentice","author":"Jane Author"}'
curl -s -X POST http://localhost:5000/api/books/the-dragons-apprentice/upload -F "file=@/tmp/test-lore.txt"
```

### Step 4: Wait for conversion + auto lore generation

```bash
# Poll book status — should go: converting → ready → generating-lore → lore-ready
for i in $(seq 1 12); do
  STATUS=$(curl -s http://localhost:5000/api/books/the-dragons-apprentice | grep -o '"status":"[^"]*"')
  echo "Attempt $i: $STATUS"
  if echo "$STATUS" | grep -q "lore-ready\|error"; then break; fi
  sleep 10
done
```

Expected: Status reaches `"lore-ready"`.

### Step 5: Verify wiki files

```bash
# List lore files
curl -s http://localhost:5000/api/books/the-dragons-apprentice/lore

# Read each wiki file
curl -s http://localhost:5000/api/books/the-dragons-apprentice/lore/characters.md
curl -s http://localhost:5000/api/books/the-dragons-apprentice/lore/locations.md
curl -s http://localhost:5000/api/books/the-dragons-apprentice/lore/themes.md
curl -s http://localhost:5000/api/books/the-dragons-apprentice/lore/summary.md
```

Expected: All 4 files exist with structured content (characters like Elara/Thormund/Finn/Malachar, locations like Thornhaven/Crystal Mountains, etc.).

### Step 6: Verify chat uses wiki context

```bash
# Chat should include wiki context
timeout 60 curl -s -N http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"bookSlug":"the-dragons-apprentice","message":"Who are the main characters?"}' 2>&1 | head -10
```

Expected: Response references characters extracted in wiki.

### Step 7: Clean up

```bash
docker compose down -v
```
