# Knowledge Engine — Status Audit & Gap Plan

## Audit Date: 2026-04-18

---

## Part 1: Feature Audit

### Core Infrastructure

| Feature | Status | Evidence |
|---------|--------|----------|
| Docker Compose (3 containers) | ✅ Working | `docker-compose.yml` — api, frontend, agent |
| .NET 8 API backend | ✅ Working | 17 endpoints across 6 endpoint files |
| EF Core + SQLite | ✅ Working | `AppDbContext.cs` — Books, ConversionJobs tables |
| Hangfire background jobs | ✅ Working | SQLite storage, continuations, dashboard at `/hangfire` |
| Named volumes (library + sqlite) | ✅ Working | `library-data`, `sqlite-data` |
| Health endpoint | ✅ Working | `GET /api/health` |

### Library & Books

| Feature | Status | Evidence |
|---------|--------|----------|
| Book CRUD | ✅ Working | `LibraryEndpoints.cs` — POST, GET list, GET detail, DELETE |
| Book-as-folder storage | ✅ Working | `/library/{slug}/book.md` |
| Title validation | ✅ Working | Rejects empty titles (400) |
| Slug generation | ✅ Working | `GenerateSlug()` — lowercase, alphanumeric, hyphens |
| Slug collision handling | ✅ Working | `DbUpdateException` catch → 409 |
| Slug validation (path traversal) | ✅ Working | All endpoints check `..`, `/`, `\` |
| **Delete button in UI** | ❌ Missing | Backend endpoint exists, no frontend button |
| **Book edit/rename** | ❌ Missing | No PATCH/PUT endpoint for book metadata |

### File Upload & Conversion

| Feature | Status | Evidence |
|---------|--------|----------|
| File upload endpoint | ✅ Working | `POST /api/books/{slug}/upload` — multipart, 100MB limit |
| Extension whitelist | ✅ Working | 13 formats: EPUB, PDF, DOCX, TXT, HTML, etc. |
| MarkItDown conversion | ✅ Working | `markitdown[all]` — 18 converters |
| Auto-conversion after upload | ✅ Working | Hangfire job + continuation |
| Upload + create combined | ✅ Working | Library page — one-step flow with file attach |
| Drag-and-drop on library | ✅ Working | `<svelte:window ondrop>` |
| Upload progress bar | ✅ Working | XHR progress events → percentage |
| Conversion status panel | ✅ Working | Elapsed time, job counts, Hangfire link |
| Auto-polling on book page | ✅ Working | Detects `converting` state, polls every 2s |
| Empty output detection | ✅ Working | Continuation checks file size > 0 |
| Conversion status API | ✅ Working | `GET /api/books/{slug}/upload/status` |
| Hangfire dashboard | ✅ Working | `/hangfire` — unlocked in Development |

### Editor

| Feature | Status | Evidence |
|---------|--------|----------|
| Milkdown rich text editor | ✅ Working | Nord theme, CommonMark + GFM |
| Theme CSS | ✅ Working | `@milkdown/theme-nord/style.css` |
| Content reactivity | ✅ Working | `$effect` watches content prop changes |
| Debounced save (1s) | ✅ Working | `setTimeout` with cleanup |
| Readonly/read-write toggle | ✅ Working | Edit/Lock button |
| **Custom editor toolbar** | ❌ Missing | No formatting buttons (bold, italic, headings, etc.) |

### Chat & AI

| Feature | Status | Evidence |
|---------|--------|----------|
| Pi Agent RPC sidecar | ✅ Working | tsx runtime, DeepSeek via models.json |
| Agent health check | ✅ Working | `/health` with `agentAlive` boolean |
| Agent graceful shutdown | ✅ Working | SIGTERM/SIGINT → kill child process |
| Agent restart limit | ✅ Working | 5 attempts max |
| Non-streaming prompt | ✅ Working | Collects from events until `agent_end` |
| SSE streaming prompt | ✅ Working | Callback before send, `\n\n` framing, 5-min timeout |
| Chat endpoint (SSE) | ✅ Working | `POST /api/chat` → streams to frontend |
| Chat panel with streaming | ✅ Working | `ChatPanel.svelte` — auto-scroll, typing indicator |
| Chat error display | ✅ Working | `chatError` state + red error div |
| Chat SSE error handling | ✅ Working | `res.ok` check, `onError` callback |
| **Chat history persistence** | ❌ Missing | Messages lost on page navigation |
| **Multiple conversations** | ❌ Missing | Single thread per book |

### Lore / Knowledge Base

| Feature | Status | Evidence |
|---------|--------|----------|
| Lore generation endpoint | ✅ Working | `POST /api/books/{slug}/lore` → Hangfire job |
| Lore file listing | ✅ Working | `GET /api/books/{slug}/lore` |
| Lore content viewer | ✅ Working | `LorePanel.svelte` — file selector + display |
| Lore extraction skill | ✅ Working | `skills/lore-extraction/SKILL.md` |
| Polling timeout (2 min) | ✅ Working | 24 × 5s = 120s |
| Lore error display | ✅ Working | `loreError` state + red error div |
| **Lore auto-refresh** | ❌ Missing | Must manually click "Generate" each time |
| **Lore versioning** | ❌ Missing | Regeneration overwrites previous lore |

### Configuration & DevEx

| Feature | Status | Evidence |
|---------|--------|----------|
| DeepSeek provider config | ✅ Working | `agent/models.json` with `openai-completions` |
| API key passthrough | ✅ Working | 6 provider keys in docker-compose.yml |
| PI_MODEL override | ✅ Working | Env var → `--model` flag |
| CORS configured | ✅ Working | localhost:5173 + localhost:5000 |
| Quiet logging | ✅ Working | EF Core/Hangfire/Hosting suppressed |
| Agent event logging | ✅ Working | Detailed: tool calls, usage, thinking, responses |
| `.env.example` | ✅ Working | DeepSeek, Anthropic, OpenAI, Google, OpenRouter options |

---

## Part 2: Dead Code & Tech Debt

| Item | File | Impact |
|------|------|--------|
| `ConversionJob` model | `Models/ConversionJob.cs` | Table in DB, `DbSet` registered, **never written to or read**. Dead schema. |
| `ValidateSlug()` method | `LibraryEndpoints.cs:95` | Defined but **never called**. Inline validation used instead. |
| `index.ts` barrel file | `frontend/src/lib/index.ts` | Empty comment. Nothing exported. |
| `appsettings.Development.json` | `backend/src/.../appsettings.Development.json` | Exists but may override production settings if `ASPNETCORE_ENVIRONMENT` changes. |
| `ChatEndpoints` — no slug validation | `ChatEndpoints.cs` | The `bookSlug` from request body goes directly into `Path.Combine` without `..`/`/` check. Path traversal possible via chat API. |

---

## Part 3: Bug & Risk Register

| Severity | Issue | File | Detail |
|----------|-------|------|--------|
| 🔴 High | **Chat path traversal** | `ChatEndpoints.cs:23,25` | `bookSlug` from request body used in `Path.Combine` without validation. A malicious `bookSlug: "../../etc/passwd"` could read arbitrary files. |
| 🟡 Medium | **No file size check on upload** | `UploadEndpoints.cs` | `file.Length == 0` is checked, but no upper bound in the endpoint itself. The `RequestSizeLimitAttribute` caps at 100MB but there's no user-friendly error for files over the limit. |
| 🟡 Medium | **ConversionJob retry storm** | `ConversionEndpoints.cs` | If conversion fails, Hangfire retries with no cap. Old retries can race with new uploads. The `[AutomaticRetry(Attempts = 0)]` attribute is not set. |
| 🟡 Medium | **No Hangfire retry limit** | `UploadEndpoints.cs` | Same issue — failed upload conversions retry indefinitely. |
| 🟡 Medium | **Thinking tokens shown as blank** | `ChatPanel.svelte` | DeepSeek Reasoner sends `thinking_delta` events. The SSE parser in `api.ts` only forwards `text_delta`. The user sees nothing during the thinking phase, making it look broken for long-running reasoning. |
| 🟢 Low | **`createError` never cleared** | `+page.svelte:49` | Once an error appears on book creation, it persists until page navigation. |
| 🟢 Low | **No favicon** | `frontend/src/app.html:4` | References `favicon.png` in static/ but file doesn't exist. |
| 🟢 Low | **Svelte-check errors (12)** | Various | Pre-existing TS type issues (`slug: string | undefined`, Milkdown type mismatches). Don't affect runtime. |

---

## Part 4: Implementation Plan for Gaps

### Task 1: Fix Chat Path Traversal [Critical]

**Files:** `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs`

Add slug validation at the start of the chat handler:

```csharp
// After deserializing the request body
if (string.IsNullOrWhiteSpace(req.BookSlug) || req.BookSlug.Contains("..") || req.BookSlug.Contains('/') || req.BookSlug.Contains('\\'))
    return Results.BadRequest(new { error = "Invalid book slug" });
```

### Task 2: Add Delete Button to UI

**Files:** `frontend/src/lib/components/BookList.svelte`, `frontend/src/lib/api.ts`

- Add a delete icon/button next to each book in `BookList.svelte`
- Call `api.deleteBook(slug)` on click
- Add confirmation (`confirm("Delete this book?")`)
- Remove the book from the list on success
- `api.deleteBook()` already exists in `api.ts`

### Task 3: Show Thinking Status for Reasoning Models

**Files:** `frontend/src/lib/api.ts`, `frontend/src/lib/components/ChatPanel.svelte`

- In `api.ts` chat SSE handler, also forward `thinking_delta` events — accumulate thinking text
- Add a new `onThinking` callback or fold it into the existing `onChunk` with a type indicator
- In `ChatPanel.svelte`, show a collapsible "Thinking..." section while the model reasons
- When `text_delta` starts, collapse the thinking section and show the actual response

### Task 4: Cap Hangfire Retries

**Files:** `backend/src/KnowledgeEngine.Api/Endpoints/ConversionEndpoints.cs`, `backend/src/KnowledgeEngine.Api/Endpoints/UploadEndpoints.cs`

Add `[AutomaticRetry(Attempts = 0)]` or set `Attempts = 1` on the Hangfire job enqueue to prevent retry storms:

```csharp
var jobId = jobClient.Create<IConversionService>(x =>
    x.ConvertToMarkdownAsync(filePath, outputPath, CancellationToken.None),
    new CreateJobOptions { /* no retry */ });
```

Or use the `[AutomaticRetry]` attribute approach. Either way, conversion jobs should not retry — the continuation handles error state.

### Task 5: Clean Up Dead Code

**Files:**
- Delete: `backend/src/KnowledgeEngine.Api/Models/ConversionJob.cs`
- Delete: `backend/src/KnowledgeEngine.Api/Data/AppDbContext.cs` — remove `DbSet<ConversionJob>` line
- Delete: `frontend/src/lib/index.ts`
- Remove: `ValidateSlug()` method from `LibraryEndpoints.cs`
- Add a new EF Core migration to drop the `ConversionJobs` table

### Task 7: Markdown Rendering in Chat Panel

**Files:** `frontend/src/lib/components/ChatPanel.svelte`

The chat panel currently renders assistant messages as plain text with `{msg.text}`. Since the AI returns markdown (headings, lists, code blocks, bold, etc.), it should be rendered.

- Install a lightweight markdown renderer (`marked` — tiny, no dependencies)
- Use `marked.parse(msg.text)` to render assistant messages as HTML
- Style the rendered markdown to match the editor theme (code blocks, headings, lists)
- Keep user messages as plain text

```bash
npm install marked
```

In ChatPanel.svelte:
- Import `import { marked } from 'marked'`
- For assistant messages, use `{@html marked(msg.text)}` instead of `{msg.text}`
- Add scoped CSS for `.assistant .message-text :global(h1)`, `:global(h2)`, `:global(ul)`, `:global(ol)`, `:global(code)`, `:global(pre)`, `:global(blockquote)`, etc.

### Task 6: Polish — Favicon, Error Clearing

**Files:**
- Create: `frontend/static/favicon.png` (simple book icon)
- `frontend/src/routes/+page.svelte` — clear `createError` when `showCreateForm` opens
