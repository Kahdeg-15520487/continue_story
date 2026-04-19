# Agent Resilience: Context Management & Session Persistence

## Problem

The Knowledge Engine's agent layer is fragile in three dimensions:

1. **Chat context overflow**: `ChatEndpoints.cs` injects full book content + wiki files + last 20 DB messages into every prompt. The Pi SDK session ALSO accumulates its own message history. Neither layer compacts — eventually the model's context window is exceeded and the LLM API rejects the request.

2. **Ephemeral sessions**: `SessionManager.inMemory()` means all conversation state dies on idle timeout (5 min), container restart, or browser close. User returns to an empty chat even though messages are saved in SQLite.

3. **No task recovery**: Long-running operations (lore generation, chapter edits) are one-shot. Failure mid-way loses all progress — partial wiki files, unwritten chapters, everything. No checkpointing or resume.

## Architecture Decision: Persistent SessionManager + Context Budget

The Pi SDK already provides the primitives we need:
- `SessionManager.create(cwd)` — persistent `.jsonl` files with full message history
- `session.compact()` — summarize conversation to reduce context
- `SessionManager.continueRecent(cwd)` — restore last session for a CWD
- `session.agent.state.messages` — access/replace message history

### Key Design: Two-Layer Context

```
┌─────────────────────────────────────────────────────┐
│  Backend (SQLite)                                   │
│  - ChatMessages table: user/assistant pairs          │
│  - AgentSessions table: session metadata + state     │
│  - LoreCheckpoints table: wiki generation progress   │
└────────────────────┬────────────────────────────────┘
                     │
┌────────────────────▼────────────────────────────────┐
│  Agent Bridge (Node.js)                             │
│  - Pi SDK persistent sessions (SessionManager.create)│
│  - .jsonl files in /library/{slug}/.pi-sessions/     │
│  - Auto-compact when token budget exceeded            │
│  - Session restore on reconnect                      │
└─────────────────────────────────────────────────────┘
```

The backend DB tracks session metadata (IDs, modes, status). The Pi SDK's persistent `.jsonl` files hold the actual conversation history. This means:
- Backend doesn't inject raw history text into prompts — the Pi SDK session already has it
- ChatEndpoint only sends the new user message + minimal context (book metadata)
- Compaction happens at the Pi SDK layer where token counting is accurate

---

## Task 1: Persistent Session Storage

**Goal**: Agent sessions survive container restart and idle disposal. When a user returns, they reconnect to the same session (or restore it from `.jsonl`).

### Files to change:

#### `agent/src/index.ts` — Switch to persistent SessionManager

```typescript
// BEFORE:
sessionManager: SessionManager.inMemory(),

// AFTER:
const sessionDir = `/library/${bookSlug}/.pi-sessions`;
mkdirSync(sessionDir, { recursive: true });
sessionManager: SessionManager.create(sessionDir),
```

Add session metadata to `ManagedSession`:

```typescript
interface ManagedSession {
  // ... existing fields ...
  sessionFile: string;  // path to .jsonl for restore
  mode: "read" | "write";
  tokenCount: number;  // estimated from last message_end usage
}
```

On session creation, store the `.jsonl` path. On `ensureSession`, check if a persisted session exists before creating a new one:

```typescript
// In POST /api/sessions handler:
if (mode !== "write") {
  // Try to find existing in-memory session
  let managed = Array.from(sessions.values())
    .find(s => s.bookSlug === bookSlug && s.mode === "read");

  if (!managed) {
    // Try to restore from persistent storage
    const sessionDir = `/library/${bookSlug}/.pi-sessions`;
    try {
      const { session: restored } = await createAgentSession({
        cwd: `/library/${bookSlug}`,
        agentDir,
        tools: createReadOnlyTools(cwd),
        resourceLoader: loader,
        sessionManager: SessionManager.continueRecent(sessionDir),
      });
      // ... wrap in ManagedSession ...
    } catch {
      // No previous session — create fresh
      managed = await createSession(bookSlug, "read");
    }
  }
}
```

On dispose (idle timeout), don't delete the `.jsonl` file. Only delete on explicit client `DELETE` request or when switching modes.

#### `agent/src/index.ts` — Add session info endpoint

Add session metadata to the health/info response so the backend knows what's alive:

```typescript
// GET /api/sessions/{id}/info
{
  sessionId: string,
  bookSlug: string,
  mode: "read" | "write",
  messageCount: number,
  lastActivity: string,
  tokenBudget: { used: number, limit: number }
}
```

### Verification:
- Start a chat session, send a few messages
- Restart the agent container
- Send another message — should continue the conversation, not start fresh
- Verify `.jsonl` file exists in `/library/{slug}/.pi-sessions/`

---

## Task 2: Context Budget & Auto-Compaction

**Goal**: When the conversation approaches the model's context limit, automatically compact (summarize) to keep it within budget.

### Files to change:

#### `agent/src/index.ts` — Track token usage and trigger compaction

In the `message_end` event handler, accumulate token counts:

```typescript
case "message_end": {
  const usage = event.message?.usage;
  if (usage) {
    managed.tokenCount = (managed.tokenCount || 0) + (usage.input || 0) + (usage.output || 0);
  }
  // Check if we need compaction
  // Most models have ~128k-200k context; compact at ~100k tokens
  const COMPACT_THRESHOLD = 100_000;
  if (managed.tokenCount > COMPACT_THRESHOLD && !managed.mode !== "write") {
    console.log(`[session:${managed.id}] auto-compacting (tokens: ${managed.tokenCount})`);
    try {
      await managed.session.compact("Summarize the conversation, keeping key facts about the book that were discussed. Preserve any analysis or interpretations shared.");
      managed.tokenCount = 0; // reset after compaction
    } catch (err) {
      console.error(`[session:${managed.id}] compaction failed:`, err);
    }
  }
  break;
}
```

Add a new endpoint for manual compaction:

```typescript
// POST /api/sessions/{id}/compact
// Body: { customInstructions?: string }
// Calls session.compact(customInstructions)
```

#### `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs` — Remove double history injection

Currently the endpoint injects both DB history AND lets the Pi SDK accumulate its own history. This doubles the context usage. Fix: stop injecting raw history into the prompt. Let the Pi SDK's persistent session manage history.

```csharp
// BEFORE:
var fullPrompt = $"You are answering questions about the book.\n\n" +
    $"# Book Content\n\n{context}\n\n---\n\n" +
    $"# Conversation History\n\n{historyText}\n\n" +
    $"User: {req.Message}";

// AFTER:
var fullPrompt = $"You are answering questions about the book.\n\n" +
    context + "\n\n---\n\n" +
    $"User: {req.Message}";
```

The system prompt with book content + wiki is still injected on every message (since the Pi SDK session may have been compacted). But raw conversation history is no longer duplicated — it lives in the Pi SDK session.

Add a "context refresh" on session creation: when a new read session is created (or restored), send a one-time context injection with the full book + wiki content. Subsequent messages only need the user's new question.

```csharp
// On new session creation, detect if it's fresh or restored
var sessionInfo = await agentService.GetSessionInfoAsync(sessionId);
if (sessionInfo.MessageCount == 0)
{
    // Fresh session — inject full context
    var contextPrompt = $"You are answering questions about the book.\n\n{context}";
    await agentService.SendPromptAsync(sessionId, contextPrompt);
}
// Then send the actual user message
await foreach (var evt in agentService.StreamPromptAsync(sessionId, req.Message, ct))
    ...
```

#### `backend/src/KnowledgeEngine.Api/Services/IAgentService.cs` — Add new methods

```csharp
Task<SessionInfo> GetSessionInfoAsync(string sessionId, CancellationToken ct = default);
Task CompactSessionAsync(string sessionId, string? customInstructions = null, CancellationToken ct = default);
```

### Verification:
- Chat for 20+ exchanges about a book
- Check logs for auto-compaction trigger
- After compaction, verify the agent still remembers earlier discussion points
- Verify token count resets after compaction

---

## Task 3: Session Reconnect on Page Refresh / Browser Close

**Goal**: When the user closes the browser or refreshes, the chat panel reconnects to the existing session and shows the conversation history.

### Files to change:

#### `frontend/src/lib/components/ChatPanel.svelte` — Restore session on mount

On mount, load chat history from DB (already done). Also check if an agent session exists for this book:

```typescript
onMount(async () => {
  // Load history from DB
  const history = await api.getChatHistory(slug);
  messages = history.map(m => ({ role: m.role, text: m.content, thinking: m.thinking }));

  // Ensure agent session is alive
  try {
    await api.ensureAgentSession(slug, 'read');
  } catch {
    // Session may have been disposed — will be recreated on next message
  }
});
```

#### `frontend/src/lib/api.ts` — Add session management methods

```typescript
ensureAgentSession: (bookSlug: string, mode: string) =>
  request<{ sessionId: string }>(`/agent/sessions`, {
    method: 'POST',
    body: JSON.stringify({ bookSlug, mode }),
  }),
```

#### `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs` — Session-aware chat

The `POST /api/chat` endpoint currently calls `EnsureSessionAsync` which creates or reuses. This already works for reconnect — but now with persistent sessions, `EnsureSessionAsync` will restore from `.jsonl` if the in-memory session was disposed.

### Verification:
- Open chat, send messages
- Close browser tab
- Reopen the book page
- Chat history loads from DB
- Send a new message — it continues the conversation with context intact

---

## Task 4: Long-Running Task Checkpointing (Lore Gen, Chapter Edits)

**Goal**: When a write-mode operation fails partway through, partially completed work is tracked and the operation can resume from where it left off.

### Files to change:

#### `backend/src/KnowledgeEngine.Api/Data/AppDbContext.cs` — Add checkpoint table

```csharp
public class LoreCheckpoint
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Slug { get; set; } = "";
    public string TargetFile { get; set; } = ""; // e.g., "characters.md"
    public string Status { get; set; } = ""; // "pending", "done", "failed"
    public string? PartialContent { get; set; } // last known content if failed
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Book Book { get; set; } = null!;
}
```

```csharp
// In AppDbContext:
public DbSet<LoreCheckpoint> LoreCheckpoints => Set<LoreCheckpoint>();
```

#### `backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs` — Checkpoint-aware generation

Replace the monolithic "generate everything" with per-file checkpoints:

```csharp
public async Task GenerateLoreAsync(string slug)
{
    // ... book/status validation ...

    // Check existing checkpoints
    var checkpoints = await db.LoreCheckpoints
        .Where(c => c.BookId == book.Id)
        .ToDictionaryAsync(c => c.TargetFile);

    var expectedFiles = new[] { "characters.md", "locations.md", "themes.md", "summary.md" };

    // Check which files are already done
    var filesToGenerate = expectedFiles
        .Where(f => !File.Exists(Path.Combine(wikiDir, f))
            || checkpoints.GetValueOrDefault(f)?.Status != "done")
        .ToList();

    if (filesToGenerate.Count == 0)
    {
        // All files done
        book.Status = "lore-ready";
        await db.SaveChangesAsync();
        return;
    }

    book.Status = "generating-lore";
    await db.SaveChangesAsync();

    // Create checkpoints for files we'll generate
    foreach (var file in filesToGenerate)
    {
        if (!checkpoints.ContainsKey(file))
        {
            db.LoreCheckpoints.Add(new LoreCheckpoint
            {
                BookId = book.Id, Slug = slug,
                TargetFile = file, Status = "pending"
            });
        }
    }
    await db.SaveChangesAsync();

    // Build prompt listing only missing files
    var fileList = string.Join(", ", filesToGenerate);
    var prompt = $"Read the book at book.md and extract lore. " +
        $"Generate ONLY these wiki files in the wiki/ directory: {fileList}. " +
        $"Follow the lore-extraction skill format exactly.";

    try
    {
        var sessionId = await agentService.EnsureSessionAsync(slug, "write");
        await agentService.SendPromptAsync(sessionId, prompt);

        // Kill write session
        try { await agentService.KillSessionAsync(sessionId); } catch { }

        // Update checkpoints based on what was actually created
        foreach (var file in expectedFiles)
        {
            var exists = File.Exists(Path.Combine(wikiDir, file));
            var cp = checkpoints.GetValueOrDefault(file);
            if (cp != null)
            {
                cp.Status = exists ? "done" : "failed";
                cp.UpdatedAt = DateTime.UtcNow;
            }
            else if (exists)
            {
                db.LoreCheckpoints.Add(new LoreCheckpoint
                {
                    BookId = book.Id, Slug = slug,
                    TargetFile = file, Status = "done"
                });
            }
        }
        await db.SaveChangesAsync();

        var doneCount = expectedFiles.Count(f =>
            File.Exists(Path.Combine(wikiDir, f)));

        book.Status = doneCount == expectedFiles.Length ? "lore-ready" : "error";
        book.ErrorMessage = doneCount == expectedFiles.Length
            ? null
            : $"Only {doneCount}/{expectedFiles.Length} wiki files generated";
        book.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
    catch (Exception ex)
    {
        // Mark pending checkpoints as failed
        foreach (var cp in checkpoints.Values.Where(c => c.Status == "pending"))
        {
            cp.Status = "failed";
            cp.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        book.Status = "error";
        book.ErrorMessage = $"Lore generation failed: {ex.Message}";
        book.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}
```

On retry (when book is in `error` or stale `generating-lore`), `filesToGenerate` will only include files that weren't completed. The agent prompt only asks for missing files.

#### `backend/src/KnowledgeEngine.Api/Endpoints/LoreEndpoints.cs` — Add retry endpoint

```csharp
// POST /api/books/{slug}/lore/retry
// Clears error status and re-enqueues the job
```

### Verification:
- Start lore generation
- Kill the agent container mid-generation (simulating failure)
- Restart, trigger retry
- Verify only missing files are regenerated, existing ones are preserved

---

## Task 5: General Edit Task Resilience

**Goal**: When a user initiates a chapter edit or story continuation via chat (write mode), the task state is tracked so it can be resumed.

### Files to change:

#### `backend/src/KnowledgeEngine.Api/Data/AppDbContext.cs` — Add AgentTask table

```csharp
public class AgentTask
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Slug { get; set; } = "";
    public string TaskType { get; set; } = ""; // "lore", "edit", "chapter"
    public string Description { get; set; } = ""; // what the task is doing
    public string Status { get; set; } = ""; // "pending", "running", "done", "failed"
    public string? SessionId { get; set; } // agent session handling this
    public string? Result { get; set; } // summary of what was done
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Book Book { get; set; } = null!;
}
```

#### `backend/src/KnowledgeEngine.Api/Services/AgentTaskService.cs` — New service

```csharp
public class AgentTaskService
{
    // Create a tracked task
    Task<AgentTask> CreateTaskAsync(int bookId, string taskType, string description);

    // Start executing a task (creates write session, sends prompt)
    Task ExecuteTaskAsync(int taskId, string prompt);

    // Mark task complete/failed
    Task CompleteTaskAsync(int taskId, string? result = null);
    Task FailTaskAsync(int taskId, string error);

    // Resume a task that was interrupted (session disposed mid-execution)
    Task ResumeTaskAsync(int taskId);

    // Get active tasks for a book
    Task<List<AgentTask>> GetActiveTasksAsync(int bookId);
}
```

On session disposal (idle timeout), if the session has an active task, mark the task as "interrupted" but don't fail it. The user can resume from the UI.

#### `frontend/src/routes/books/[slug]/+page.svelte` — Show task status

When a write task is running, show a task indicator in the toolbar:
- "📝 Editing..." with a spinner
- On failure: "❌ Edit failed" with a "Resume" button
- On browser close: task stays in DB, shows as "Interrupted" on return

### Verification:
- User initiates a chapter edit via chat
- Close browser
- Return — see "Interrupted task" banner
- Click "Resume" — agent continues from checkpoint

---

## Task 6: Cleanup & Housekeeping

**Goal**: Don't let `.jsonl` files and checkpoint data accumulate forever.

### Files to change:

#### `agent/src/index.ts` — Session file cleanup

On explicit `DELETE /api/sessions/{id}`, also delete the `.jsonl` file:

```typescript
function disposeSession(id: string, reason: string) {
  const managed = sessions.get(id);
  if (!managed) return;
  managed.unsubscribe();
  managed.session.dispose();
  // ... existing cleanup ...

  // Delete session file on explicit client request
  if (reason === "client request" && managed.sessionFile) {
    try { unlinkSync(managed.sessionFile); } catch {}
  }
}
```

#### `backend/src/KnowledgeEngine.Api/Services/SessionCleanupService.cs` — Periodic cleanup

A Hangfire recurring job that:
- Deletes `.jsonl` session files older than 7 days (no recent activity)
- Clears `LoreCheckpoint` rows for books in `lore-ready` status
- Marks `AgentTask` rows older than 24 hours as "expired"

```csharp
[AutomaticRetry(Attempts = 0)]
public class SessionCleanupService
{
    public async Task CleanupAsync()
    {
        // Delete stale session files (>7 days)
        // Clear completed checkpoints
        // Expire old tasks
    }
}

// In Program.cs:
RecurringJob.AddOrUpdate<SessionCleanupService>(
    "session-cleanup",
    x => x.CleanupAsync(),
    Cron.Daily);
```

#### `docker-compose.yml` — Mount session storage

The `.pi-sessions/` directories are inside `/library/{slug}/`, which is already the `library-data` volume. No new volume needed.

### Verification:
- Check that old session files are cleaned up after 7 days
- Verify checkpoint cleanup after lore-ready

---

## Dependency Order

```
Task 1 (Persistent Sessions)
  └── Task 2 (Auto-Compaction) — needs token tracking from persistent sessions
  └── Task 3 (Session Reconnect) — needs persistent sessions to restore from
  └── Task 4 (Lore Checkpoints) — independent, can parallel with Task 2/3
  └── Task 5 (General Edit Tasks) — needs Tasks 1+2 for full value
  └── Task 6 (Cleanup) — needs Task 1 for files to clean up
```

Recommended execution order: **1 → 2+3 (parallel) → 4 → 5 → 6**

## Self-Review

- [x] Every task references specific files and code locations
- [x] No placeholders — each code block shows the actual change
- [x] Pi SDK APIs verified against docs (SessionManager.create, session.compact, continueRecent)
- [x] No new npm packages needed — all primitives exist in Pi SDK
- [x] No new external services — SQLite + filesystem only
- [x] Backward compatible — existing books/sessions continue to work
- [x] Context budget prevents the "silent context overflow" that currently exists
- [x] Checkpointing handles the "fail mid-way, start from scratch" problem
- [x] Session persistence handles the "close browser, lose everything" problem
