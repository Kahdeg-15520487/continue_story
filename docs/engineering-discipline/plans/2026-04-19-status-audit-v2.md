# Knowledge Engine — Status Audit & Gap Plan (v2)

**Audit Date:** 2026-04-19  
**Since last audit (2026-04-18):** +7 commits — chat history DB persistence, thinking block persistence, delete button in UI, markdown chat rendering, path traversal fix, Hangfire retry cap, dead code cleanup

---

## Part 1: Feature Audit

### Infrastructure

| Feature | Status | Detail |
|---------|--------|--------|
| Docker Compose (3 containers) | ✅ | api, frontend, agent |
| .NET 8 API (17 endpoints) | ✅ | 7 endpoint files |
| EF Core + SQLite (2 tables) | ✅ | Books, ChatMessages |
| Hangfire background jobs | ✅ | SQLite storage, retry capped at 0 |
| Named volumes | ✅ | library-data, sqlite-data |
| Health endpoint | ✅ | `GET /api/health` |
| Hangfire dashboard | ✅ | `/hangfire` in Development, no auth |
| Quiet logging | ✅ | EF Core/Hangfire/Hosting suppressed |
| CORS (both origins) | ✅ | localhost:5173 + localhost:5000 |

### Library & Books

| Feature | Status | Detail |
|---------|--------|--------|
| Book CRUD (4 endpoints) | ✅ | POST, GET list, GET detail, DELETE |
| Book-as-folder storage | ✅ | `/library/{slug}/book.md` |
| Title validation | ✅ | Rejects empty (400) |
| Slug generation + collision (409) | ✅ | |
| Path traversal protection | ✅ | All endpoints check `..`, `/`, `\` |
| Delete button in UI | ✅ | Hover reveal in BookList, with confirm |
| **Book rename/edit** | ❌ | No PATCH/PUT for book metadata |

### File Upload & Conversion

| Feature | Status | Detail |
|---------|--------|--------|
| File upload endpoint (100MB) | ✅ | `POST /api/books/{slug}/upload` |
| Extension whitelist (13 formats) | ✅ | EPUB, PDF, DOCX, TXT, HTML, etc. |
| MarkItDown `[all]` (18 converters) | ✅ | |
| Auto-conversion after upload | ✅ | Hangfire job + continuation |
| Upload + create combined flow | ✅ | One-step on library page |
| Drag-and-drop on library page | ✅ | `<svelte:window ondrop>` |
| Upload progress bar | ✅ | XHR progress events |
| Conversion status panel | ✅ | Elapsed time, job counts, Hangfire link |
| Conversion status API | ✅ | `GET /api/books/{slug}/upload/status` |
| Auto-polling on book page | ✅ | Detects `converting`, polls every 2s |
| Empty output detection | ✅ | Continuation checks file size > 0 |
| **Re-upload / replace file** | ❌ | Upload always creates new conversion, no way to re-upload after error |

### Editor

| Feature | Status | Detail |
|---------|--------|--------|
| Milkdown rich text editor | ✅ | Nord theme CSS, CommonMark + GFM |
| Content reactivity | ✅ | `$effect` recreates editor on prop change |
| Debounced save (1s) | ✅ | |
| Readonly/read-write toggle | ✅ | Edit/Lock button |
| **Custom editor toolbar** | ❌ | No formatting buttons (bold, italic, headings) |
| **Editor collaboration/multi-user** | ❌ | Out of scope |

### Chat & AI

| Feature | Status | Detail |
|---------|--------|--------|
| Pi Agent RPC sidecar | ✅ | DeepSeek via models.json, `openai-completions` |
| Agent health + graceful shutdown | ✅ | SIGTERM/SIGINT, restart limit (5) |
| SSE streaming prompt | ✅ | Callback before send, `\n\n` framing, 5-min timeout |
| Non-streaming prompt | ✅ | Collects from events until `agent_end` |
| Chat endpoint (SSE) | ✅ | `POST /api/chat` |
| Chat panel with streaming | ✅ | Auto-scroll, typing indicator |
| Chat error display | ✅ | `chatError` + red error div |
| Chat SSE error handling | ✅ | `res.ok` check, `onError` callback |
| Thinking status (reasoning models) | ✅ | Live spinner + collapsible thinking text |
| Thinking persistence | ✅ | Saved with message, collapsible `<details>` |
| Markdown rendering in chat | ✅ | `marked` — headings, code blocks, lists, tables |
| **Chat history persistence** | ✅ | **NEW** — DB table, load on mount, save after exchange |
| **Clear chat history** | ✅ | **NEW** — button in chat header + `DELETE` endpoint |
| **Chat context includes lore** | ✅ | Book content + wiki files sent as context |
| **Multi-turn conversation context** | ❌ | Each message is independent — no conversation history sent to the LLM |

### Lore / Knowledge Base

| Feature | Status | Detail |
|---------|--------|--------|
| Lore generation endpoint | ✅ | `POST /api/books/{slug}/lore` → Hangfire |
| Lore file listing + content viewer | ✅ | LorePanel with file selector |
| Lore extraction skill | ✅ | `skills/lore-extraction/SKILL.md` |
| Polling timeout (2 min) | ✅ | 24 × 5s |
| **Lore auto-refresh on content change** | ❌ | Must manually trigger each time |
| **Lore versioning** | ❌ | Regeneration overwrites previous lore |

### Configuration & DevEx

| Feature | Status | Detail |
|---------|--------|--------|
| DeepSeek provider (models.json) | ✅ | Custom provider via `openai-completions` |
| API key passthrough (6 providers) | ✅ | Anthropic, OpenAI, Google, OpenRouter, DeepSeek, base URL |
| PI_MODEL override | ✅ | `--model` flag |
| Agent event logging | ✅ | Detailed: tool calls, usage, thinking, responses |
| `.env.example` | ✅ | 5 provider options documented |

---

## Part 2: Dead Code & Tech Debt

| Item | Severity | File |
|------|----------|------|
| Agent has 25 `console.log` statements | 🟢 Low | `agent/src/index.ts` — these are structured bridge logs, appropriate for a containerized process |
| Frontend has 3 `console.error` | 🟢 Low | Error catches that also have UI display |
| Missing favicon | 🟢 Low | `app.html` references `favicon.png` — file doesn't exist |
| `appsettings.Development.json` may override logging in prod | 🟢 Low | `ASPNETCORE_ENVIRONMENT=Development` is hardcoded in docker-compose |
| 12 pre-existing svelte-check TS errors | 🟢 Low | `$page.params.slug: string | undefined` and Milkdown type mismatches — no runtime impact |

---

## Part 3: Bug & Risk Register

| Severity | Issue | File | Detail |
|----------|-------|------|--------|
| 🟡 Medium | **No multi-turn conversation** | `ChatEndpoints.cs` | Each chat message is sent to the LLM as a standalone prompt with no prior conversation history. The LLM has no memory of previous messages in the session. |
| 🟡 Medium | **No re-upload after error** | `UploadEndpoints.cs` | If conversion fails (status=`error`), the upload endpoint can re-upload. But the old source file is not cleaned up. Multiple source files can accumulate in the book directory. |
| 🟡 Medium | **Lore generation has no book content check** | `LoreJobService.cs` | Lore generation proceeds even if `book.md` doesn't exist or is empty. The agent gets no context and produces garbage lore. |
| 🟢 Low | **`createError` never cleared** | `+page.svelte` | Cleared when form opens, but persists after a failed creation until user re-opens the form. |
| 🟢 Low | **Chat history loads all messages** | `ChatHistoryEndpoints.cs` | No pagination. Very long conversations will load everything at once. |

---

## Part 4: Implementation Plan for Remaining Gaps

### Task 1: Multi-Turn Conversation Context

**Dependencies:** None  
**Effort:** Medium  
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs`

The chat endpoint currently constructs a single-shot prompt. It should load recent chat history from the DB and include it as conversation context.

In `ChatEndpoints.cs`, before constructing `fullPrompt`:
```csharp
// Load recent conversation history
var chatHistory = await db.ChatMessages
    .Where(m => m.BookId == book.Id)
    .OrderByDescending(m => m.CreatedAt)
    .Take(20)
    .OrderBy(m => m.CreatedAt)
    .ToListAsync();

var historyText = string.Join("\n\n", chatHistory.Select(m =>
    m.Role == "user" ? $"User: {m.Content}" : $"Assistant: {m.Content}"));

var fullPrompt = $"You are answering questions about the book.\n\n" +
    $"# Book Content\n\n{context}\n\n---\n\n" +
    $"# Conversation History\n\n{historyText}\n\n" +
    $"User: {req.Message}";
```

Also need to inject `AppDbContext` into the chat endpoint handler.

### Task 2: Re-Upload Cleanup

**Dependencies:** None  
**Effort:** Small  
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/UploadEndpoints.cs`

Before saving the new file, delete the old source file if it exists:
```csharp
// Clean up old source file if re-uploading
if (!string.IsNullOrEmpty(book.SourceFile))
{
    var oldPath = Path.Combine(bookDir, book.SourceFile);
    if (File.Exists(oldPath) && oldPath != filePath)
        File.Delete(oldPath);
}
```

### Task 3: Lore Generation Guard

**Dependencies:** None  
**Effort:** Small  
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs`

Add a check at the start of `GenerateLoreAsync`:
```csharp
var bookMd = Path.Combine(libraryPath, book.Slug, "book.md");
if (!File.Exists(bookMd) || new FileInfo(bookMd).Length == 0)
{
    book.Status = "error";
    book.ErrorMessage = "Cannot generate lore: book has no content";
    book.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    _logger.LogError("Lore generation skipped: no book.md for {Slug}", book.Slug);
    return;
}
```

### Task 4: Favicon + Polish

**Dependencies:** None  
**Effort:** Trivial  
**Files:**
- Create: `frontend/static/favicon.png`
- Modify: `frontend/src/routes/+page.svelte` — clear `createError` on navigation

### Task 5: Chat History Pagination

**Dependencies:** None  
**Effort:** Small  
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ChatHistoryEndpoints.cs`
- Modify: `frontend/src/lib/api.ts`
- Modify: `frontend/src/lib/types.ts`

Add `?limit=N` query parameter to the GET endpoint. Default to last 100 messages.
