# Refactor Agent Session Management Plan

> **Worker note:** Execute this plan task-by-task using the agentic-run-plan skill or subagents. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Remove the read/write mode distinction — always give the agent write tools with a system prompt instructing it not to modify files unless asked. Add session management UI (New Session button + session dropdown).

**Architecture:** The agent container always creates sessions with `createCodingTools(cwd)` (full read/write access). A system prompt tells the agent: "You may only modify files when the user explicitly asks you to edit, rewrite, or create content." The backend no longer detects edit intent or switches modes — it just manages a single persistent session per book. The frontend gets session management controls (New Session + dropdown to switch between recent sessions). Chat history in the DB is keyed by `sessionId` so switching sessions also switches displayed messages.

**Tech Stack:** Svelte 5, ASP.NET Minimal APIs, Pi SDK agent sessions, SQLite (EF Core)

**Work Scope:**
- **In scope:**
  - Remove read/write mode distinction from agent container
  - Remove edit intent detection from ChatEndpoints.cs
  - Always use write tools, rely on system prompt for safety
  - Simplify ChatEndpoints.cs: single context injection path, no mode branching
  - Add `sessionId` to ChatMessage model and chat history API
  - Add session management endpoints (list sessions, create new session, switch session)
  - Add session management UI to ChatPanel (New Session button, session dropdown)
  - Remove the separate write session creation/kill lifecycle
  - Keep DiffOverlay integration (edit_done event) working
- **Out of scope:**
  - Changing the Pi SDK session persistence mechanism
  - Adding named/renamed sessions
  - Changing the inline edit flow (that's separate from chat)
  - Changing models or model configuration

**Verification Strategy:**
- **Level:** build + manual curl test
- **Command:** `cd backend && dotnet build KnowledgeEngine.sln` (backend), `cd frontend && npx vite build` (frontend)
- **What it validates:** Code compiles, endpoints respond correctly

---

## File Structure Mapping

| File | Responsibility | Action |
|---|---|---|
| `agent/src/index.ts` | Agent session server | Modify: remove mode, always use write tools, add list-sessions-by-book endpoint |
| `backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs` | Chat request DTO | Modify: add `SessionId` field, remove mode concept |
| `backend/src/KnowledgeEngine.Api/Models/ChatMessage.cs` | Chat message DB model | Modify: add `SessionId` column |
| `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs` | Chat SSE endpoint | Rewrite: remove mode/edit-intent, single context path, accept sessionId |
| `backend/src/KnowledgeEngine.Api/Endpoints/AgentEndpoints.cs` | Agent session proxy endpoints | Modify: add session listing by book, new-session endpoint |
| `backend/src/KnowledgeEngine.Api/Services/AgentService.cs` | Agent HTTP proxy | Modify: remove mode param from EnsureSessionAsync, add list sessions method |
| `backend/src/KnowledgeEngine.Api/Services/IAgentService.cs` | Agent service interface | Modify: match AgentService changes |
| `frontend/src/lib/api.ts` | API client | Modify: add session management methods, pass sessionId in chat |
| `frontend/src/lib/components/ChatPanel.svelte` | Chat panel UI | Modify: add session dropdown + New Session button |
| `frontend/src/routes/books/[slug]/+page.svelte` | Main book page | No changes needed (ChatPanel is self-contained) |

---

### Task 1: Simplify agent container — remove mode distinction

**Dependencies:** None
**Files:**
- Modify: `agent/src/index.ts`

- [ ] **Step 1: Remove mode from session creation**

In `agent/src/index.ts`, find the `createSession` function. Currently it takes `mode: "read" | "write"` and selects tools based on mode. Change the signature and body:

Find:
```typescript
async function createSession(bookSlug: string, mode: "read" | "write"): Promise<ManagedSession> {
```

Replace with:
```typescript
async function createSession(bookSlug: string): Promise<ManagedSession> {
```

Find the tools selection:
```typescript
  const tools = mode === "write" ? createCodingTools(cwd) : createReadOnlyTools(cwd);
```

Replace with:
```typescript
  const tools = createCodingTools(cwd);
```

Find the skills override:
```typescript
  const loader = new DefaultResourceLoader({
    cwd,
    agentDir,
    skillsOverride: mode === "write"
      ? (current) => ({ skills: current.skills, diagnostics: current.diagnostics })
      : (current) => ({ skills: [], diagnostics: current.diagnostics }),
  });
```

Replace with:
```typescript
  const loader = new DefaultResourceLoader({
    cwd,
    agentDir,
  });
```

Remove the `createReadOnlyTools` import since it's no longer used. Find:
```typescript
  createCodingTools,
  createReadOnlyTools,
```

Replace with:
```typescript
  createCodingTools,
```

- [ ] **Step 2: Update ManagedSession interface — remove mode**

Find in the `ManagedSession` interface:
```typescript
  mode: "read" | "write";
```

Replace with:
```typescript
  mode: string;
```

- [ ] **Step 3: Update createSession body — remove mode from managed object**

Find in `createSession`:
```typescript
  const managed: ManagedSession = {
    id,
    bookSlug,
    mode,
    session,
```

Replace with:
```typescript
  const managed: ManagedSession = {
    id,
    bookSlug,
    mode: "read-write",
    session,
```

- [ ] **Step 4: Update restoreSession — use write tools**

In the `restoreSession` function, find:
```typescript
    const tools = createReadOnlyTools(cwd);
    const loader = new DefaultResourceLoader({
      cwd,
      agentDir,
      skillsOverride: (current) => ({ skills: [], diagnostics: current.diagnostics }),
    });
```

Replace with:
```typescript
    const tools = createCodingTools(cwd);
    const loader = new DefaultResourceLoader({
      cwd,
      agentDir,
    });
```

Find in the managed object in `restoreSession`:
```typescript
      mode: "read",
```

Replace with:
```typescript
      mode: "read-write",
```

- [ ] **Step 5: Update POST /api/sessions handler — remove mode branching**

Find the session creation handler in `handleRequest`:
```typescript
      const { bookSlug, mode } = JSON.parse(body);
      if (!bookSlug || bookSlug.includes("..") || bookSlug.includes("/") || bookSlug.includes("\\")) {
        sendError(res, 400, "Invalid book slug");
        return;
      }

      let managed: ManagedSession | undefined;

      if (mode !== "write") {
        // Try to reuse existing in-memory session
        managed = Array.from(sessions.values())
          .find(s => s.bookSlug === bookSlug && s.mode === "read");

        // No in-memory session — try to restore from persistent storage
        if (!managed) {
          managed = (await restoreSession(bookSlug)) ?? undefined;
        }
      }

      if (!managed) {
        managed = await createSession(bookSlug, mode === "write" ? "write" : "read");
      } else {
        resetIdleTimer(managed);
      }
```

Replace with:
```typescript
      const { bookSlug } = JSON.parse(body);
      if (!bookSlug || bookSlug.includes("..") || bookSlug.includes("/") || bookSlug.includes("\\")) {
        sendError(res, 400, "Invalid book slug");
        return;
      }

      let managed: ManagedSession | undefined;

      // Try to reuse existing in-memory session for this book
      managed = Array.from(sessions.values())
        .find(s => s.bookSlug === bookSlug);

      // No in-memory session — try to restore from persistent storage
      if (!managed) {
        managed = (await restoreSession(bookSlug)) ?? undefined;
      }

      if (!managed) {
        managed = await createSession(bookSlug);
      } else {
        resetIdleTimer(managed);
      }
```

- [ ] **Step 6: Update auto-compaction — run for all sessions, not just read**

Find:
```typescript
      // Auto-compact for read sessions exceeding threshold
      if (session.mode === "read" && session.tokenCount > COMPACT_THRESHOLD_TOKENS) {
```

Replace with:
```typescript
      if (session.tokenCount > COMPACT_THRESHOLD_TOKENS) {
```

- [ ] **Step 7: Add list-sessions-by-book endpoint**

Find the `// List sessions` handler (GET /api/sessions). After it, add a new endpoint for listing sessions by book:

After the closing `}` of the `// List sessions` block (the one that returns all sessions), add:

```typescript

  // List sessions for a book
  const bookSessionsMatch = url.pathname.match(/^\/api\/books\/([^/]+)\/sessions$/);
  if (bookSessionsMatch && req.method === "GET") {
    const bookSlug = bookSessionsMatch[1];
    const bookSessions = Array.from(sessions.values())
      .filter(s => s.bookSlug === bookSlug)
      .map(s => ({
        id: s.id,
        bookSlug: s.bookSlug,
        age: Math.round((Date.now() - s.createdAt) / 1000) + "s",
        idle: Math.round((Date.now() - s.lastActivity) / 1000) + "s",
        tokenCount: s.tokenCount,
      }));
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({ sessions: bookSessions }));
    return;
  }
```

- [ ] **Step 8: Remove mode-specific session file cleanup**

Find in `disposeSession`:
```typescript
  // Delete session files on explicit client request
  if (reason === "client request") {
    try {
      const sessionDir = getSessionDir(managed.bookSlug);
      // Remove all .jsonl files in this book's session dir
      if (managed.mode === "write") {
        // For write sessions, just dispose — the lore job may need the session
      } else {
        // For read sessions, clean up all session files
        try {
          const files = readdirSync(sessionDir).filter(f => f.endsWith(".jsonl"));
          for (const f of files) {
            unlinkSync(join(sessionDir, f));
          }
          console.log(`[session:${id}] cleaned up ${files.length} session files`);
        } catch {}
      }
    } catch {}
  }
```

Replace with:
```typescript
  if (reason === "client request") {
    try {
      const sessionDir = getSessionDir(managed.bookSlug);
      try {
        const files = readdirSync(sessionDir).filter(f => f.endsWith(".jsonl"));
        for (const f of files) {
          unlinkSync(join(sessionDir, f));
        }
        console.log(`[session:${id}] cleaned up ${files.length} session files`);
      } catch {}
    } catch {}
  }
```

- [ ] **Step 9: Verify agent starts**

Run: `docker compose up -d --build agent`
Wait 10 seconds, then: `curl -s http://localhost:5000/api/books | head -c 100`

Expected: API responds with book list (agent dependency satisfied)

- [ ] **Step 10: Commit**

```bash
cd J:/workspace2/llm/continue_story_4
git add agent/src/index.ts
git commit -m "refactor(agent): remove read/write mode distinction, always use write tools"
```

---

### Task 2: Update backend models — add SessionId to ChatMessage

**Dependencies:** None (can run in parallel with Task 1)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Models/ChatMessage.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs`
- Create: `backend/src/KnowledgeEngine.Api/Data/Migrations/AddSessionIdToChatMessage.cs` (or use auto-migration)

- [ ] **Step 1: Add SessionId to ChatMessage**

Find in `backend/src/KnowledgeEngine.Api/Models/ChatMessage.cs`:
```csharp
    public string Content { get; set; }
```

After it, add:
```csharp
    public string? SessionId { get; set; }
```

- [ ] **Step 2: Add SessionId to ChatRequest**

Find in `backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs`:
```csharp
    public string? ActiveChapterId { get; set; }
```

After it, add:
```csharp
    public string? SessionId { get; set; }
```

- [ ] **Step 3: Update EF Core DbContext if needed**

Find the `ChatMessages` DbSet in the DbContext file. Check if there's any fluent configuration for `ChatMessage` in `OnModelCreating`. If there is, add `.HasIndex(m => m.SessionId)`.

- [ ] **Step 4: Add EF Core migration for the new column**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet ef migrations add AddSessionIdToChatMessage --project src/KnowledgeEngine.Api --startup-project src/KnowledgeEngine.Api`

If `dotnet ef` is not installed, skip this step — SQLite will auto-create the column if the DB is recreated. Instead, add this to the DbContext's `OnModelCreating` or `EnsureCreated` setup to handle the schema change:

Find `EnsureCreated` or the migration setup in the DbContext. Add after the existing ensure/initialize code:

```csharp
// Add SessionId column if it doesn't exist (SQLite migration)
try
{
    database.ExecuteSqlRaw("ALTER TABLE ChatMessages ADD COLUMN SessionId TEXT NULL");
}
catch { /* Column already exists */ }
```

- [ ] **Step 5: Verify build**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
cd J:/workspace2/llm/continue_story_4
git add backend/src/KnowledgeEngine.Api/Models/ChatMessage.cs backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs
git commit -m "refactor: add SessionId to ChatMessage and ChatRequest models"
```

---

### Task 3: Update AgentService — remove mode, add session listing

**Dependencies:** Task 1 (agent container removes mode)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/IAgentService.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Services/AgentService.cs`

- [ ] **Step 1: Update IAgentService interface**

Find in `backend/src/KnowledgeEngine.Api/Services/IAgentService.cs`:
```csharp
    Task<string> EnsureSessionAsync(string bookSlug, string mode = "read", CancellationToken ct = default);
```

Replace with:
```csharp
    Task<string> EnsureSessionAsync(string bookSlug, CancellationToken ct = default);
    Task<string> CreateNewSessionAsync(string bookSlug, CancellationToken ct = default);
    Task<List<SessionSummary>> ListSessionsAsync(string bookSlug, CancellationToken ct = default);
```

Add a new record to the interface:
```csharp
    public record SessionSummary(string Id, string BookSlug, string Age, string Idle, int TokenCount);
```

- [ ] **Step 2: Update AgentService implementation**

Find in `backend/src/KnowledgeEngine.Api/Services/AgentService.cs`:
```csharp
    public async Task<string> EnsureSessionAsync(string bookSlug, string mode = "read", CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring session for book: {Slug} (mode: {Mode})", bookSlug, mode);

        var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions",
            new StringContent(JsonSerializer.Serialize(new { bookSlug, mode }), Encoding.UTF8, "application/json"),
            ct);
```

Replace with:
```csharp
    public async Task<string> EnsureSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring session for book: {Slug}", bookSlug);

        var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions",
            new StringContent(JsonSerializer.Serialize(new { bookSlug }), Encoding.UTF8, "application/json"),
            ct);
```

Add the new methods after `CompactSessionAsync`:

```csharp
    public async Task<string> CreateNewSessionAsync(string bookSlug, CancellationToken ct = default)
    {
        _logger.LogInformation("Creating new session for book: {Slug}", bookSlug);

        // Kill any existing in-memory sessions for this book first
        var existing = await ListSessionsAsync(bookSlug, ct);
        foreach (var s in existing)
        {
            try { await KillSessionAsync(s.Id, ct); } catch { }
        }

        var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions",
            new StringContent(JsonSerializer.Serialize(new { bookSlug, forceNew = true }), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        return result.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Agent returned no sessionId");
    }

    public async Task<List<SessionSummary>> ListSessionsAsync(string bookSlug, CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_agentBaseUrl}/api/books/{bookSlug}/sessions", ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<JsonElement>(json);
            var sessions = result.GetProperty("sessions");

            var list = new List<SessionSummary>();
            foreach (var s in sessions.EnumerateArray())
            {
                list.Add(new SessionSummary(
                    s.GetProperty("id").GetString()!,
                    s.GetProperty("bookSlug").GetString()!,
                    s.GetProperty("age").GetString()!,
                    s.GetProperty("idle").GetString()!,
                    s.GetProperty("tokenCount").GetInt32()
                ));
            }
            return list;
        }
        catch
        {
            return new List<SessionSummary>();
        }
    }
```

- [ ] **Step 3: Update agent container to support forceNew**

In `agent/src/index.ts`, find the POST /api/sessions handler. Find:

```typescript
      let managed: ManagedSession | undefined;

      // Try to reuse existing in-memory session for this book
      managed = Array.from(sessions.values())
        .find(s => s.bookSlug === bookSlug);
```

Replace with:

```typescript
      const { bookSlug, forceNew } = JSON.parse(body);

      let managed: ManagedSession | undefined;

      // If forceNew, skip reuse and create fresh
      if (!forceNew) {
        // Try to reuse existing in-memory session for this book
        managed = Array.from(sessions.values())
          .find(s => s.bookSlug === bookSlug);
      }
```

Also update the destructuring line above (remove the old one):
Find:
```typescript
      const { bookSlug } = JSON.parse(body);
```

This was already replaced by the new destructuring above. Make sure there's only one `const { bookSlug...` line.

- [ ] **Step 4: Verify build**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
cd J:/workspace2/llm/continue_story_4
git add backend/src/KnowledgeEngine.Api/Services/IAgentService.cs backend/src/KnowledgeEngine.Api/Services/AgentService.cs agent/src/index.ts
git commit -m "refactor(agent-service): remove mode, add CreateNewSession and ListSessions"
```

---

### Task 4: Rewrite ChatEndpoints — remove mode/edit-intent, single context path

**Dependencies:** Task 2 (ChatRequest needs SessionId), Task 3 (AgentService needs new signatures)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs`

- [ ] **Step 1: Rewrite ChatEndpoints.cs**

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

            if (Directory.Exists(chaptersDir))
            {
                var chapterFiles = Directory.GetFiles(chaptersDir, "ch-*.md")
                    .Where(f => !f.EndsWith(".scratch.md"))
                    .OrderBy(f => f)
                    .ToArray();

                if (chapterFiles.Length > 0)
                {
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

            // Active chapter
            if (!string.IsNullOrEmpty(req.ActiveChapterId) && Directory.Exists(chaptersDir))
            {
                var activeFile = Path.Combine(chaptersDir, $"{req.ActiveChapterId}.md");
                if (File.Exists(activeFile))
                {
                    var activeChapterContent = await File.ReadAllTextAsync(activeFile, ct);
                    var activeChapterTitle = activeChapterContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? req.ActiveChapterId;
                    var truncated = activeChapterContent.Length > 30_000
                        ? activeChapterContent[..30_000] + "\n\n[... truncated ...]"
                        : activeChapterContent;
                    contextParts.Add($"# Current Chapter: {activeChapterTitle}\n\n{truncated}");
                }
            }

            // Referenced chapters (by name/number in user message)
            if (!string.IsNullOrEmpty(req.Message) && Directory.Exists(chaptersDir))
            {
                var chapterFiles = Directory.GetFiles(chaptersDir, "ch-*.md")
                    .Where(f => !f.EndsWith(".scratch.md"))
                    .OrderBy(f => f)
                    .ToArray();

                if (chapterFiles.Length > 0)
                {
                    var msgLower = req.Message.ToLowerInvariant();
                    foreach (var chFile in chapterFiles)
                    {
                        var chContent = await File.ReadAllTextAsync(chFile, ct);
                        var title = chContent.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?.Substring(2) ?? "";
                        var fileName = Path.GetFileNameWithoutExtension(chFile);

                        if (fileName == req.ActiveChapterId) continue;

                        var match = false;
                        if (!string.IsNullOrEmpty(title) && msgLower.Contains(title.ToLowerInvariant()))
                            match = true;
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
            }

            // Wiki files
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

            // ── Agent session ──────────────────────────────────────────────
            string sessionId;

            if (!string.IsNullOrEmpty(req.SessionId))
            {
                sessionId = req.SessionId;
            }
            else
            {
                sessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
            }

            // Inject context on fresh sessions
            try
            {
                var info = await agentService.GetSessionInfoAsync(sessionId, ct);
                if (info.MessageCount == 0)
                {
                    var contextPrompt = new StringBuilder()
                        .AppendLine("You are an AI assistant helping with a book. You can answer questions about the story, characters, and themes using the context below.")
                        .AppendLine()
                        .AppendLine("You also have the ability to modify story content. When the user explicitly asks you to edit, rewrite, modify, or create content, write the complete modified file to the path they specify. Do NOT modify files unless the user explicitly asks.")
                        .AppendLine()
                        .AppendLine(context)
                        .ToString();
                    await agentService.SendPromptAsync(sessionId, contextPrompt, ct);
                }
            }
            catch
            {
                try
                {
                    await agentService.SendPromptAsync(sessionId, $"You are helping with a book.\n\n{context}", ct);
                }
                catch { }
            }

            // ── Save user message ──────────────────────────────────────────
            if (book is not null)
            {
                db.ChatMessages.Add(new ChatMessage
                {
                    BookId = book.Id,
                    Role = "user",
                    Content = req.Message,
                    SessionId = sessionId,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // ── Stream response ────────────────────────────────────────────
            var assistantText = "";
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, req.Message, ct))
            {
                await response.WriteAsync($"data: {evt}\n\n", ct);
                await response.Body.FlushAsync(ct);

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

            // ── Check for scratch file ─────────────────────────────────────
            // If the agent wrote a .scratch.md file for the active chapter, send edit_done
            if (!string.IsNullOrEmpty(req.ActiveChapterId))
            {
                var scratchFile = Path.Combine(libraryPath, req.BookSlug, "chapters", $"{req.ActiveChapterId}.scratch.md");
                if (File.Exists(scratchFile))
                {
                    var doneEvent = JsonSerializer.Serialize(new
                    {
                        type = "edit_done",
                        chapterId = req.ActiveChapterId,
                        scratchPath = $"chapters/{req.ActiveChapterId}.scratch.md",
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
                    SessionId = sessionId,
                    CreatedAt = DateTime.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            }

            // ── Return session ID to frontend ─────────────────────────────
            var sessionEvent = JsonSerializer.Serialize(new { type = "session_info", sessionId });
            await response.WriteAsync($"data: {sessionEvent}\n\n", ct);
            await response.Body.FlushAsync(ct);
        });
    }
}
```

Key changes:
- Removed `EditIntentKeywords` and `DetectEditIntent`
- Removed `mode` variable and mode-based branching
- Single context injection path for all requests
- System prompt tells agent not to modify files unless explicitly asked
- Always checks for `.scratch.md` after streaming (not just in "write mode")
- Passes `sessionId` back to frontend via `session_info` SSE event
- Stores `sessionId` on `ChatMessage` records
- Uses `req.SessionId` if provided (for session switching)

- [ ] **Step 2: Verify build**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
cd J:/workspace2/llm/continue_story_4
git add backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs
git commit -m "refactor(chat): remove mode/edit-intent, single context path, session-aware"
```

---

### Task 5: Add session management endpoints to backend

**Dependencies:** Task 3 (AgentService methods)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs` (add new endpoints)
- Modify: `backend/src/KnowledgeEngine.Api/Data/AppDbContext.cs` (add chat query by session)

- [ ] **Step 1: Add session management and chat history endpoints**

Add these endpoints to `ChatEndpoints.cs`, inside the `Map` method, after the main `/api/chat` endpoint:

```csharp
        // ── Session management ────────────────────────────────────────────

        // Get current/new session for a book
        app.MapGet("/api/books/{slug}/chat/session", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessionId = await agentService.EnsureSessionAsync(slug, ct);
            return Results.Ok(new { sessionId });
        });

        // Create a new session (kills old one)
        app.MapPost("/api/books/{slug}/chat/session/new", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessionId = await agentService.CreateNewSessionAsync(slug, ct);
            return Results.Ok(new { sessionId });
        });

        // List active sessions for a book
        app.MapGet("/api/books/{slug}/chat/sessions", async (
            string slug,
            IAgentService agentService,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid book slug" });

            var sessions = await agentService.ListSessionsAsync(slug, ct);
            return Results.Ok(new { sessions });
        });

        // ── Chat history (session-aware) ──────────────────────────────────

        // Get chat history (optionally filtered by session)
        app.MapGet("/api/books/{slug}/chat", async (
            string slug,
            string? sessionId,
            int limit,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.ChatMessages
                .Include(m => m.Book)
                .Where(m => m.Book.Slug == slug);

            if (!string.IsNullOrEmpty(sessionId))
                query = query.Where(m => m.SessionId == sessionId);

            var messages = await query
                .OrderByDescending(m => m.CreatedAt)
                .Take(limit > 0 ? limit : 100)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Role, m.Content, m.SessionId, m.CreatedAt })
                .ToListAsync(ct);

            return Results.Ok(messages);
        });

        // Clear chat history (optionally by session)
        app.MapDelete("/api/books/{slug}/chat", async (
            string slug,
            string? sessionId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.ChatMessages
                .Include(m => m.Book)
                .Where(m => m.Book.Slug == slug);

            if (!string.IsNullOrEmpty(sessionId))
                query = query.Where(m => m.SessionId == sessionId);

            var messages = await query.ToListAsync(ct);
            db.ChatMessages.RemoveRange(messages);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new { cleared = true, count = messages.Count });
        });
```

- [ ] **Step 2: Verify build**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
cd J:/workspace2/llm/continue_story_4
git add backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs
git commit -m "feat(chat): add session management and session-aware chat history endpoints"
```

---

### Task 6: Update frontend API client for session management

**Dependencies:** None (can run in parallel with Tasks 1-5)
**Files:**
- Modify: `frontend/src/lib/api.ts`

- [ ] **Step 1: Update chat method to accept and return sessionId**

Find the `chat()` method signature and add `sessionId` to the options:

```typescript
    options?: { activeChapterId?: string | null; onEditDone?: (chapterId: string) => void },
```

Replace with:

```typescript
    options?: { activeChapterId?: string | null; sessionId?: string | null; onEditDone?: (chapterId: string) => void; onSessionInfo?: (sessionId: string) => void },
```

Find the request body:
```typescript
      body: JSON.stringify({
        bookSlug,
        message,
        activeChapterId: options?.activeChapterId ?? null,
      }),
```

Replace with:
```typescript
      body: JSON.stringify({
        bookSlug,
        message,
        activeChapterId: options?.activeChapterId ?? null,
        sessionId: options?.sessionId ?? null,
      }),
```

Add handling for `session_info` SSE event. In the SSE event parsing loop, alongside the existing `edit_done` check, add:

```typescript
                if (editParsed.type === 'session_info' && editParsed.sessionId) {
                  options?.onSessionInfo?.(editParsed.sessionId);
                }
```

- [ ] **Step 2: Add session management API methods**

After the existing `ensureAgentSession` method, add:

```typescript
  getChatSession: (slug: string) =>
    request<{ sessionId: string }>(`/books/${slug}/chat/session`),

  createNewChatSession: (slug: string) =>
    request<{ sessionId: string }>(`/books/${slug}/chat/session/new`, { method: 'POST' }),

  listChatSessions: (slug: string) =>
    request<{ sessions: { id: string; bookSlug: string; age: string; idle: string; tokenCount: number }[] }>(`/books/${slug}/chat/sessions`),
```

Update `getChatHistory` to accept optional `sessionId`:

Find:
```typescript
  getChatHistory: (slug: string, limit: number = 100) =>
    request<{ role: string; content: string; sessionId?: string; createdAt: string }[]>(`/books/${slug}/chat?limit=${limit}`),
```

Replace with:
```typescript
  getChatHistory: (slug: string, limit: number = 100, sessionId?: string) =>
    request<{ role: string; content: string; sessionId?: string; createdAt: string }[]>(`/books/${slug}/chat?limit=${limit}${sessionId ? `&sessionId=${encodeURIComponent(sessionId)}` : ''}`),
```

Update `clearChatHistory` to accept optional `sessionId`:

Find:
```typescript
  clearChatHistory: (slug: string) =>
    request<{ cleared: boolean }>(`/books/${slug}/chat`, { method: 'DELETE' }),
```

Replace with:
```typescript
  clearChatHistory: (slug: string, sessionId?: string) =>
    request<{ cleared: boolean }>(`/books/${slug}/chat${sessionId ? `?sessionId=${encodeURIComponent(sessionId)}` : ''}`, { method: 'DELETE' }),
```

- [ ] **Step 3: Verify frontend builds**

Run: `cd J:/workspace2/llm/continue_story_4/frontend && npx vite build`
Expected: No errors

- [ ] **Step 4: Commit**

```bash
cd J:/workspace2/llm/continue_story_4
git add frontend/src/lib/api.ts
git commit -m "feat(chat-api): add session management methods, sessionId support"
```

---

### Task 7: Update ChatPanel with session management UI

**Dependencies:** Task 6 (api.ts needs new methods)
**Files:**
- Modify: `frontend/src/lib/components/ChatPanel.svelte`

- [ ] **Step 1: Add session state variables**

After the existing state declarations (around `let chatError = $state('')`), add:

```typescript
  let currentSessionId = $state<string | null>(null);
  let showSessionMenu = $state(false);
```

- [ ] **Step 2: Update onMount to get/create session**

Find the `onMount` block. Update the session initialization. Currently it does:
```typescript
    await api.ensureAgentSession(slug, 'read');
```

Replace with:
```typescript
    const sessionResult = await api.getChatSession(slug);
    currentSessionId = sessionResult.sessionId;
```

- [ ] **Step 3: Update chat history loading to use sessionId**

Find where chat history is loaded:
```typescript
    const history = await api.getChatHistory(slug);
```

Replace with:
```typescript
    const history = await api.getChatHistory(slug, 100, currentSessionId ?? undefined);
```

- [ ] **Step 4: Update send() to pass sessionId and handle session_info**

Find the `api.chat(` call. Update the options argument to include `sessionId` and `onSessionInfo`:

```typescript
      { activeChapterId, onEditDone, sessionId: currentSessionId, onSessionInfo: (id) => { currentSessionId = id; } }
```

- [ ] **Step 5: Add new session handler**

Add a new function after the `send()` function:

```typescript
  async function startNewSession() {
    try {
      const result = await api.createNewChatSession(slug);
      currentSessionId = result.sessionId;
      messages = [];
      chatError = '';
    } catch (err: any) {
      chatError = err.message || 'Failed to create new session';
    }
  }
```

- [ ] **Step 6: Add session dropdown UI**

In the template, find the panel header section. Currently it looks like:

```svelte
<div class="panel-header">
  <h3 class="panel-title">AI Chat</h3>
  <button class="btn-clear-history" title="Clear chat history" onclick={...}>Clear</button>
</div>
```

Replace the panel header with:

```svelte
<div class="panel-header">
  <h3 class="panel-title">AI Chat</h3>
  <div class="session-controls">
    <button class="btn-new-session" title="New session" onclick={startNewSession}>+</button>
    <button class="btn-clear-history" title="Clear chat history" onclick={async () => { await api.clearChatHistory(slug, currentSessionId ?? undefined); messages = []; }}>Clear</button>
  </div>
</div>
```

Add styling for the new button:

```css
.session-controls {
  display: flex;
  gap: 6px;
  align-items: center;
}

.btn-new-session {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--bg-tertiary);
  color: var(--text-secondary);
  font-size: 16px;
  font-weight: 600;
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: background 0.15s;
}

.btn-new-session:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}
```

- [ ] **Step 7: Verify frontend builds**

Run: `cd J:/workspace2/llm/continue_story_4/frontend && npx vite build`
Expected: No errors

- [ ] **Step 8: Commit**

```bash
cd J:/workspace2/llm/continue_story_4
git add frontend/src/lib/components/ChatPanel.svelte
git commit -m "feat(chat-panel): add session management UI (New Session button, sessionId tracking)"
```

---

### Task 8 (Final): End-to-End Verification

**Dependencies:** All preceding tasks (1-7)
**Files:** None (read-only verification)

- [ ] **Step 1: Build backend**

Run: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build KnowledgeEngine.sln`
Expected: Build succeeded

- [ ] **Step 2: Rebuild and start containers**

Run: `cd J:/workspace2/llm/continue_story_4 && docker compose up -d --build`
Expected: All containers start

- [ ] **Step 3: Verify chat works without mode**

Open the book page, select a chapter, open chat. Send "What happens in this chapter?". Verify:
- Agent responds with chapter context
- Backend logs show no "mode: read" or "mode: write" — just a single session
- SSE events include `session_info` with a `sessionId`

- [ ] **Step 4: Verify edit still works**

Send "Rewrite the opening paragraph to be more dramatic". Verify:
- Agent writes a `.scratch.md` file
- `edit_done` SSE event fires
- DiffOverlay appears with accept/reject

- [ ] **Step 5: Verify New Session button**

Click the "+" button in the chat panel header. Verify:
- Chat clears
- New session is created (check backend logs)
- Subsequent messages use the new session

- [ ] **Step 6: Verify agent session has write tools**

Check agent container logs. Verify that sessions are created with `createCodingTools` (not `createReadOnlyTools`).
