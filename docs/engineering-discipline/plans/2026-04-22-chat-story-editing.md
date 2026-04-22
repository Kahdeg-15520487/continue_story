# Chat-Powered Story Editing Implementation Plan

> **Worker note:** Execute this plan task-by-task using the agentic-run-plan skill or subagents. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Make the chat panel contextually aware (current chapter, wiki, other chapters) and able to generate story modifications that show as a diff overlay in the editor for accept/reject.

**Architecture:** The chat panel passes `activeChapterId` to the backend. The backend injects full context (current chapter content, wiki, chapter TOC) into the agent session. When the user requests a story modification, the agent writes a `.scratch.md` file using `write` mode. The frontend detects the `edit_done` SSE event and opens the existing `DiffOverlay` component — reusing the accept/reject flow from inline edit.

**Tech Stack:** Svelte 5, ASP.NET Minimal APIs, SSE streaming, Pi SDK agent sessions (read/write modes), `diff` library (already installed)

**Work Scope:**
- **In scope:**
  - Pass `activeChapterId` from frontend to chat endpoint
  - Inject current chapter content, wiki files, and chapter TOC into agent context
  - Detect "edit intent" in user messages and switch to write mode
  - Write `.scratch.md` file via agent, stream response, send `edit_done` event
  - Frontend: show DiffOverlay when chat triggers an edit, reuse accept/reject
  - Refresh context when active chapter changes
- **Out of scope:**
  - New chat UI components (reuse existing ChatPanel)
  - Creating new chapters from chat
  - Editing wiki content from chat
  - Multi-chapter edits in a single request

**Verification Strategy:**
- **Level:** build + manual curl test
- **Command:** `dotnet build KnowledgeEngine.sln` (backend), dev server starts without errors (frontend)
- **What it validates:** Code compiles, endpoints respond, SSE streaming works

---

## File Structure Mapping

| File | Responsibility | Action |
|---|---|---|
| `backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs` | Chat request DTO | Modify: add `ActiveChapterId` field |
| `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs` | Chat SSE endpoint | Modify: inject chapter context, detect edit intent, write scratch file, emit `edit_done` |
| `frontend/src/lib/components/ChatPanel.svelte` | Chat panel UI | Modify: accept `activeChapterId` prop, emit `onEditDone` callback, pass chapter ID to API |
| `frontend/src/lib/api.ts` | API client | Modify: pass `activeChapterId` in chat request, add `onEditDone` callback type |
| `frontend/src/routes/books/[slug]/+page.svelte` | Main book page | Modify: pass `activeChapterId` and `onEditDone` to ChatPanel, handle diff display from chat |

---

### Task 1: Extend ChatRequest model

**Dependencies:** None (can run in parallel with Task 2)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs`

- [ ] **Step 1: Add ActiveChapterId to ChatRequest**

Replace the full contents of `backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs` with:

```csharp
namespace KnowledgeEngine.Api.Models;

public class ChatRequest
{
    public string BookSlug { get; set; } = "";
    public string Message { get; set; } = "";
    public string? ActiveChapterId { get; set; }
}
```

- [ ] **Step 2: Verify build**

Run: `cd backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs
git commit -m "feat(chat): add ActiveChapterId to ChatRequest model"
```

---

### Task 2: Rewrite chat endpoint with chapter context and edit capability

**Dependencies:** Task 1 (needs ChatRequest.ActiveChapterId)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs`

- [ ] **Step 1: Rewrite ChatEndpoints.cs with full context injection and edit detection**

Replace the full contents of `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs` with:

```csharp
using System.Text;
using System.Text.Json;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class ChatEndpoints
{
    /// <summary>
    /// Keywords that indicate the user wants to modify story content (not just ask questions).
    /// </summary>
    private static readonly string[] EditIntentKeywords = new[]
    {
        "rewrite", "modify", "change", "edit", "revise", "rephrase",
        "rewrite this chapter", "change the", "make it", "update the",
        "add a scene", "remove", "delete this", "replace", "expand",
        "shorten", "condense", "fix this", "improve this", "reword"
    };

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/chat", async (ChatRequest req,
            IAgentService agentService,
            IConfiguration config,
            AppDbContext db,
            HttpContext ctx,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.BookSlug) || req.BookSlug.Contains("..") || req.BookSlug.Contains('/') || req.BookSlug.Contains('\\'))
            {
                ctx.Response.StatusCode = 400;
                await ctx.Response.WriteAsJsonAsync(new { error = "Invalid book slug" }, ct);
                return;
            }

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == req.BookSlug, ct);

            var response = ctx.Response;
            response.ContentType = "text/event-stream";
            response.Headers.Append("Cache-Control", "no-cache");
            response.Headers.Append("Connection", "keep-alive");

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var wikiDir = Path.Combine(libraryPath, req.BookSlug, "wiki");
            var chaptersDir = Path.Combine(libraryPath, req.BookSlug, "chapters");

            // ── Build context ──────────────────────────────────────────────
            var contextParts = new List<string>();
            var chapterFiles = Array.Empty<string>();
            string? activeChapterTitle = null;
            string? activeChapterContent = null;

            if (Directory.Exists(chaptersDir))
            {
                chapterFiles = Directory.GetFiles(chaptersDir, "ch-*.md")
                    .Where(f => !f.EndsWith(".scratch.md"))
                    .OrderBy(f => f)
                    .ToArray();

                if (chapterFiles.Length > 0)
                {
                    // Chapter TOC
                    var chapterList = new StringBuilder("# Chapters\n\n");
                    for (int i = 0; i < chapterFiles.Length; i++)
                    {
                        var chContent = await File.ReadAllTextAsync(chapterFiles[i], ct);
                        var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? $"Chapter {i + 1}";
                        chapterList.AppendLine($"{i + 1}. {title} ({Path.GetFileName(chapterFiles[i])})");
                    }
                    contextParts.Add(chapterList.ToString());
                }
            }

            // Active chapter content (always include if available)
            if (!string.IsNullOrEmpty(req.ActiveChapterId) && Directory.Exists(chaptersDir))
            {
                var activeFile = Path.Combine(chaptersDir, $"{req.ActiveChapterId}.md");
                if (File.Exists(activeFile))
                {
                    activeChapterContent = await File.ReadAllTextAsync(activeFile, ct);
                    activeChapterTitle = activeChapterContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? req.ActiveChapterId;
                    var truncated = activeChapterContent.Length > 30_000
                        ? activeChapterContent[..30_000] + "\n\n[... truncated ...]"
                        : activeChapterContent;
                    contextParts.Add($"# Current Chapter: {activeChapterTitle}\n\n{truncated}");
                }
            }

            // If user mentions another chapter by name/number, include it too
            if (!string.IsNullOrEmpty(req.Message) && chapterFiles.Length > 0)
            {
                var msgLower = req.Message.ToLowerInvariant();
                foreach (var chFile in chapterFiles)
                {
                    var chContent = await File.ReadAllTextAsync(chFile, ct);
                    var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "";
                    var fileName = Path.GetFileNameWithoutExtension(chFile);

                    // Skip the active chapter (already included)
                    if (fileName == req.ActiveChapterId) continue;

                    var match = false;
                    if (!string.IsNullOrEmpty(title) && msgLower.Contains(title.ToLowerInvariant()))
                        match = true;
                    // Match "chapter N"
                    if (int.TryParse(msgLower.Replace("chapter", "").Trim(), out var chNum))
                    {
                        var idx = Array.IndexOf(chapterFiles, chFile);
                        if (idx == chNum - 1) match = true;
                    }

                    if (match)
                    {
                        var truncated = chContent.Length > 15_000
                            ? chContent[..15_000] + "\n[... truncated ...]"
                            : chContent;
                        contextParts.Add($"# Referenced Chapter: {title}\n\n{truncated}");
                    }
                }
            }

            // Wiki files (always include — they're small)
            if (Directory.Exists(wikiDir))
            {
                foreach (var wikiFile in Directory.GetFiles(wikiDir, "*.md").OrderBy(f => f))
                {
                    var wikiContent = await File.ReadAllTextAsync(wikiFile, ct);
                    if (wikiContent.Length > 10_000)
                        wikiContent = wikiContent[..10_000] + "\n\n[... truncated ...]";
                    contextParts.Add($"# Wiki: {Path.GetFileName(wikiFile)}\n\n{wikiContent}");
                }
            }

            var context = string.Join("\n\n---\n\n", contextParts);

            // ── Detect edit intent ─────────────────────────────────────────
            var hasEditIntent = DetectEditIntent(req.Message ?? "");

            // ── Agent session ──────────────────────────────────────────────
            var mode = hasEditIntent ? "write" : "read";
            var sessionId = await agentService.EnsureSessionAsync(req.BookSlug, mode, ct);

            // Inject context on fresh sessions
            try
            {
                var info = await agentService.GetSessionInfoAsync(sessionId, ct);
                if (info.MessageCount == 0)
                {
                    var contextPrompt = hasEditIntent
                        ? $"You are helping edit a book. You have access to the full book context.\n\n{context}\n\nWhen the user asks you to modify or rewrite content, write the COMPLETE modified chapter to the scratch file path they specify. Keep everything else the same unless instructed otherwise."
                        : $"You are answering questions about the book. You have access to the full book context.\n\n{context}";
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
            }
            catch
            {
                // Session info failed — inject context anyway
                try
                {
                    var contextPrompt = $"You are helping with a book.\n\n{context}";
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
                catch { }
            }

            // ── Build user prompt ──────────────────────────────────────────
            string userPrompt;
            string? scratchChapterId = null;

            if (hasEditIntent && !string.IsNullOrEmpty(req.ActiveChapterId))
            {
                // Instruct agent to write scratch file
                scratchChapterId = req.ActiveChapterId;
                var scratchPath = $"chapters/{req.ActiveChapterId}.scratch.md";
                var sb = new StringBuilder();
                sb.AppendLine($"User request: {req.Message}");
                sb.AppendLine();
                sb.AppendLine($"IMPORTANT: Write the COMPLETE modified chapter to: {scratchPath}");
                sb.AppendLine("Include the full chapter content with the requested changes applied. Do not skip or summarize any parts.");
                userPrompt = sb.ToString();
            }
            else if (hasEditIntent && string.IsNullOrEmpty(req.ActiveChapterId))
            {
                // Edit intent but no chapter selected — just answer, no file write
                userPrompt = $"User: {req.Message}\n\nNote: No chapter is currently active. Tell the user to select a chapter first if they want to edit.";
            }
            else
            {
                userPrompt = $"User: {req.Message}";
            }

            // ── Save user message ──────────────────────────────────────────
            if (book is not null)
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    BookId = book.Id,
                    Role = "user",
                    Content = req.Message,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // ── Stream response ────────────────────────────────────────────
            var assistantText = "";
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, userPrompt, ct))
            {
                await response.WriteAsync($"data: {evt}\n\n", ct);
                await response.Body.FlushAsync(ct);

                // Capture assistant text for DB storage
                try
                {
                    var parsed = JsonDocument.Parse(evt);
                    if (parsed.RootElement.TryGetProperty("type", out var type) && type.GetString() == "message_update")
                    {
                        if (parsed.RootElement.TryGetProperty("assistantMessageEvent", out var asm)
                            && asm.TryGetProperty("type", out var asmType) && asmType.GetString() == "text_delta"
                            && asm.TryGetProperty("delta", out var delta))
                        {
                            assistantText += delta.GetString() ?? "";
                        }
                    }
                }
                catch { }
            }

            // ── Edit done event ────────────────────────────────────────────
            if (hasEditIntent && scratchChapterId != null)
            {
                var scratchFile = Path.Combine(libraryPath, req.BookSlug, "chapters", $"{scratchChapterId}.scratch.md");
                var scratchExists = File.Exists(scratchFile);

                if (scratchExists)
                {
                    var doneEvent = JsonSerializer.Serialize(new
                    {
                        type = "edit_done",
                        chapterId = scratchChapterId,
                        scratchPath = $"chapters/{scratchChapterId}.scratch.md",
                        source = "chat"
                    });
                    await response.WriteAsync($"data: {doneEvent}\n\n", ct);
                    await response.Body.FlushAsync(ct);
                }
            }

            // ── Save assistant response ────────────────────────────────────
            if (book is not null && !string.IsNullOrEmpty(assistantText))
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    BookId = book.Id,
                    Role = "assistant",
                    Content = assistantText,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // Kill write sessions (read sessions persist for conversation continuity)
            if (hasEditIntent)
            {
                try { await agentService.KillSessionAsync(sessionId, ct); } catch { }
            }
        });
    }

    /// <summary>
    /// Simple keyword-based edit intent detection.
    /// </summary>
    private static bool DetectEditIntent(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;
        var lower = message.ToLowerInvariant();
        return EditIntentKeywords.Any(kw => lower.Contains(kw));
    }
}
```

- [ ] **Step 2: Verify build**

Run: `cd backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs
git commit -m "feat(chat): inject chapter context, detect edit intent, write scratch files"
```

---

### Task 3: Update frontend API client for chat edits

**Dependencies:** None (can run in parallel with Tasks 1-2)
**Files:**
- Modify: `frontend/src/lib/api.ts`

- [ ] **Step 1: Update chat method signature to accept activeChapterId and onEditDone**

Find the `chat(` method in `frontend/src/lib/api.ts` (around line 85) and replace it. The current signature is:

```typescript
  chat(
    bookSlug: string,
    message: string,
    onChunk: (data: string) => void,
    onDone: () => void,
    onError?: (err: string) => void,
    onThinking?: (text: string) => void,
  ): AbortController {
```

Replace with:

```typescript
  chat(
    bookSlug: string,
    message: string,
    onChunk: (data: string) => void,
    onDone: () => void,
    onError?: (err: string) => void,
    onThinking?: (text: string) => void,
    options?: { activeChapterId?: string | null; onEditDone?: (chapterId: string) => void },
  ): AbortController {
```

In the same method, find the `body: JSON.stringify({ bookSlug, message }),` line and replace with:

```typescript
      body: JSON.stringify({
        bookSlug,
        message,
        activeChapterId: options?.activeChapterId ?? null,
      }),
```

In the SSE event parsing loop (inside the `.then(async (res) => {` block), find the section that processes `data:` lines. Currently it parses `message_update` and `thinking` events. Add a check for `edit_done` **before** the `onDone()` call. Find the line that calls `onDone()` (there should be one near the end of the SSE processing, after the `for (const line of msg.split('\n'))` loop). Before that `onDone()`, add:

```typescript
              // Check for edit_done event (chat triggered a story modification)
              try {
                const editParsed = JSON.parse(data);
                if (editParsed.type === 'edit_done' && editParsed.chapterId) {
                  options?.onEditDone?.(editParsed.chapterId);
                }
              } catch { /* not JSON, ignore */ }
```

- [ ] **Step 2: Verify frontend starts without errors**

Run: `cd frontend && npx vite build` (or just check that no TypeScript errors)
Expected: No errors

- [ ] **Step 3: Commit**

```bash
git add frontend/src/lib/api.ts
git commit -m "feat(chat-api): pass activeChapterId, add onEditDone callback"
```

---

### Task 4: Update ChatPanel to accept activeChapterId and emit edit events

**Dependencies:** Task 3 (needs updated api.chat signature)
**Files:**
- Modify: `frontend/src/lib/components/ChatPanel.svelte`

- [ ] **Step 1: Add activeChapterId prop and onEditDone callback**

Find the props declaration at the top of `<script>`:

```svelte
  let { slug }: { slug: string } = $props();
```

Replace with:

```svelte
  let {
    slug,
    activeChapterId = null,
    onEditDone,
  }: {
    slug: string;
    activeChapterId?: string | null;
    onEditDone?: (chapterId: string) => void;
  } = $props();
```

- [ ] **Step 2: Update the api.chat() call in send() to pass options**

Find the `api.chat(` call inside the `send()` function:

```typescript
    api.chat(
      slug,
      msg,
      (chunk) => { currentResponse += chunk; },
      () => {
        if (currentResponse) {
          messages = [...messages, { role: 'assistant', text: currentResponse, thinking: thinkingText || undefined }];
        }
        currentResponse = '';
        thinkingText = '';
        streaming = false;
      },
      (err) => { chatError = err; },
      (thinking) => { thinkingText += thinking; }
    );
```

Replace with:

```typescript
    api.chat(
      slug,
      msg,
      (chunk) => { currentResponse += chunk; },
      () => {
        if (currentResponse) {
          messages = [...messages, { role: 'assistant', text: currentResponse, thinking: thinkingText || undefined }];
        }
        currentResponse = '';
        thinkingText = '';
        streaming = false;
      },
      (err) => { chatError = err; },
      (thinking) => { thinkingText += thinking; },
      { activeChapterId, onEditDone }
    );
```

- [ ] **Step 3: Commit**

```bash
git add frontend/src/lib/components/ChatPanel.svelte
git commit -m "feat(chat-panel): accept activeChapterId prop, forward onEditDone"
```

---

### Task 5: Wire up ChatPanel in main page to trigger DiffOverlay

**Dependencies:** Task 4 (needs ChatPanel with onEditDone)
**Files:**
- Modify: `frontend/src/routes/books/[slug]/+page.svelte`

- [ ] **Step 1: Pass activeChapterId and onEditDone to ChatPanel**

Find the ChatPanel usage:

```svelte
          <ChatPanel {slug} />
```

Replace with:

```svelte
          <ChatPanel {slug} {activeChapterId} onEditDone={handleChatEditDone} />
```

- [ ] **Step 2: Add handleChatEditDone function**

Find the `handleRejectEdit` function (the last handler before the closing `</script>`). After its closing brace, add:

```typescript
  async function handleChatEditDone(chapterId: string) {
    // If a diff is already showing, reject it first
    if (diffState && activeChapterId) {
      try { await api.rejectInlineEdit(slug, activeChapterId); } catch { /* ignore */ }
      diffState = null;
    }

    // If the edit is for a different chapter than what's active, switch to it
    if (chapterId !== activeChapterId) {
      await handleChapterSelect(chapterId);
    }

    // Fetch scratch content and show diff
    try {
      const result = await api.getScratchContent(slug, chapterId);
      const chapter = await api.getChapter(slug, chapterId);
      if (chapter) {
        diffState = {
          original: chapter.content,
          scratch: result.content,
        };
        showInlineEdit = true;
      }
    } catch (err: any) {
      console.error('Failed to load chat edit diff:', err);
    }
  }
```

- [ ] **Step 3: Commit**

```bash
git add frontend/src/routes/books/[slug]/+page.svelte
git commit -m "feat: wire chat edit done to DiffOverlay display"
```

---

### Task 6 (Final): End-to-End Verification

**Dependencies:** All preceding tasks (1-5)
**Files:** None (read-only verification)

- [ ] **Step 1: Build backend**

Run: `cd backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 2: Rebuild and start containers**

Run: `cd /path/to/project && docker compose up -d --build`
Expected: All containers start, API responds on port 5000, frontend on 5173

- [ ] **Step 3: Verify chat sends activeChapterId**

Open browser DevTools Network tab. Open a book, select a chapter, open chat, send "What happens in this chapter?". Check the POST `/api/chat` request body contains `activeChapterId`.

- [ ] **Step 4: Verify edit intent triggers write mode and scratch file**

Send a message like "Rewrite the opening of this chapter to be more dramatic". Verify:
- Backend logs show `mode: write` for the session
- Backend logs show the agent prompt includes "Write the COMPLETE modified chapter to: chapters/..."
- A `.scratch.md` file appears in the chapters directory
- An `edit_done` SSE event is received by the frontend
- The DiffOverlay appears showing the diff between original and modified chapter

- [ ] **Step 5: Verify accept/reject works from chat-triggered diff**

Click Accept on the DiffOverlay. Verify the chapter file is replaced and the sidebar refreshes. Repeat with Reject — verify the scratch file is deleted and the chapter is unchanged.

- [ ] **Step 6: Verify context includes wiki**

Send "What are the main themes of this story?". Verify the agent response references wiki content (themes, characters, etc.) — proving wiki files were injected into context.
