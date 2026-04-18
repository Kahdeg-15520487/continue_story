# Ebook Upload Feature Implementation Plan

> **Worker note:** Execute this plan task-by-task using the agentic-run-plan skill or subagents. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Add file upload to the Knowledge Engine so users can import ebooks (EPUB, PDF, DOCX, TXT, HTML) through the browser. Upload saves the file to the library volume, sets `SourceFile` on the Book, and auto-triggers markdown conversion. The frontend shows upload progress and conversion status.

**Architecture:** Add a single `POST /api/books/{slug}/upload` endpoint that accepts multipart/form-data. The frontend gets an upload zone on the book detail page that appears when `status === "pending"`. No new services, models, or Docker changes needed — the existing `ConversionService` + `ConversionJobService` pipeline handles everything after upload.

**Tech Stack:**
- **Backend:** ASP.NET Core Minimal APIs, `IFormFile` binding, Hangfire background jobs
- **Frontend:** SvelteKit 5, native `<input type="file">` with drag-and-drop via Svelte 5 bindings
- **Infrastructure:** No changes — `library-data` volume already writable at `/library`

**Work Scope:**
- **In scope:** Backend upload endpoint with validation, frontend upload zone with progress bar, auto-conversion trigger, status polling display
- **Out of scope:** Chunked/resumable uploads, multi-file upload, file management (rename/delete source files), drag-drop on the library page, CDN/object storage

---

**Verification Strategy:**
- **Level:** build + manual curl test + frontend load
- **Commands:**
  ```
  docker compose build && docker compose up -d
  # Create book, then upload a file:
  curl -X POST http://localhost:5000/api/books/test/upload \
    -F "file=@test.epub"
  # Verify status changes: pending → converting → ready
  ```

---

## File Structure Mapping

```
backend/src/KnowledgeEngine.Api/
├── Endpoints/
│   ├── UploadEndpoints.cs              # Task 1: NEW — multipart upload endpoint
│   └── ConversionEndpoints.cs          # No changes (reuse existing)
├── Program.cs                          # Task 1: register UploadEndpoints, increase body size limit
└── appsettings.json                    # Task 1: add allowed extensions config

frontend/src/
├── lib/
│   ├── api.ts                          # Task 2: add upload() method with progress callback
│   ├── types.ts                        # Task 2: add UploadProgress type
│   └── components/
│       └── UploadZone.svelte           # Task 3: NEW — file input + drag/drop + progress bar
└── routes/
    └── books/[slug]/
        └── +page.svelte                # Task 4: integrate UploadZone, show conversion status
```

---

## Task 1: Backend Upload Endpoint

**Dependencies:** None (can run in parallel with Tasks 2–3)
**Files:**
- Create: `backend/src/KnowledgeEngine.Api/Endpoints/UploadEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Program.cs`
- Modify: `backend/src/KnowledgeEngine.Api/appsettings.json`

### Step 1: Create `UploadEndpoints.cs`

Create `backend/src/KnowledgeEngine.Api/Endpoints/UploadEndpoints.cs`:

```csharp
using Hangfire;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class UploadEndpoints
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".docx", ".doc", ".txt", ".html", ".htm",
        ".pptx", ".xlsx", ".xls", ".csv", ".ipynb", ".md"
    };

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/upload");

        group.MapPost("/", async (
            string slug,
            IFormFile file,
            AppDbContext db,
            IConfiguration config,
            IBackgroundJobClient jobClient) =>
        {
            // Validate slug
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            // Validate book exists
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null)
                return Results.NotFound(new { error = "Book not found" });

            // Validate file was provided
            if (file.Length == 0)
                return Results.BadRequest(new { error = "File is empty" });

            // Validate extension
            var extension = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(extension))
                return Results.BadRequest(new
                {
                    error = $"Unsupported file type: {extension}. Allowed: {string.Join(", ", AllowedExtensions)}"
                });

            // Save file to library volume
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            Directory.CreateDirectory(bookDir);

            // Sanitize filename — keep only the filename, no path components
            var safeFileName = Path.GetFileName(file.FileName);
            var filePath = Path.Combine(bookDir, safeFileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            // Update book metadata
            book.SourceFile = safeFileName;
            book.Status = "converting";
            book.ErrorMessage = null;
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            // Trigger conversion via Hangfire
            var outputPath = Path.Combine(bookDir, "book.md");
            var jobId = jobClient.Enqueue<IConversionService>(x =>
                x.ConvertToMarkdownAsync(filePath, outputPath, CancellationToken.None));

            // Update book status when conversion completes
            jobClient.ContinueJobWith<ConversionJobService>(jobId,
                service => service.UpdateBookAfterConversion(book.Id));

            return Results.Ok(new
            {
                book.Slug,
                sourceFile = safeFileName,
                size = file.Length,
                status = "converting",
                jobId
            });
        })
        .DisableAntiforgery()     // No CSRF for API-only project
        .WithMetadata(new RequestSizeLimitAttribute(100 * 1024 * 1024));  // 100MB limit
    }
}
```

Key design decisions:
- **Extension whitelist** — hardcoded set matching markitdown's supported formats
- **`DisableAntiforgery()`** — this is an API-only project, no CSRF tokens
- **`RequestSizeLimitAttribute(100MB)`** — per-endpoint override of the default 30MB
- **Path safety** — `Path.GetFileName()` strips any directory components from the uploaded filename
- **Auto-triggers conversion** — reuses existing `ConversionService` + `ConversionJobService` pipeline

### Step 2: Register endpoint and increase Kestrel limit in `Program.cs`

Add the endpoint registration. Find:

```csharp
app.MapConversionEndpoints();
```

Insert after:

```csharp
app.MapUploadEndpoints();
```

Add the Kestrel body size limit. Find:

```csharp
builder.Services.AddDbContext<AppDbContext>();
```

Insert before:

```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 100 * 1024 * 1024; // 100MB
});
```

### Step 3: Build and verify

```bash
cd J:/workspace2/llm/continue_story_4/backend
dotnet build KnowledgeEngine.sln
```

Expected: Build succeeds with 0 errors.

### Step 4: Commit

```bash
git add backend/src/KnowledgeEngine.Api/Endpoints/UploadEndpoints.cs backend/src/KnowledgeEngine.Api/Program.cs
git commit -m "feat(backend): add file upload endpoint with extension validation and auto-conversion"
```

---

## Task 2: Frontend API Client — Upload Method

**Dependencies:** None (can run in parallel with Task 1)
**Files:**
- Modify: `frontend/src/lib/api.ts`
- Modify: `frontend/src/lib/types.ts`

### Step 1: Add upload-related type to `types.ts`

Add at the end of `frontend/src/lib/types.ts`:

```typescript
export interface UploadResult {
  slug: string;
  sourceFile: string;
  size: number;
  status: string;
  jobId: string;
}
```

### Step 2: Add `upload()` method to `api.ts`

In `frontend/src/lib/api.ts`, add a new method to the `api` object. Find the last method in the `api` object (the `chat` method's closing `},`) and insert after it, before the closing `};` of the `api` object:

```typescript

  // Upload file for a book
  upload(
    slug: string,
    file: File,
    onProgress?: (percent: number) => void
  ): Promise<UploadResult> {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      const formData = new FormData();
      formData.append('file', file);

      xhr.upload.addEventListener('progress', (e) => {
        if (e.lengthComputable && onProgress) {
          onProgress(Math.round((e.loaded / e.total) * 100));
        }
      });

      xhr.addEventListener('load', () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            resolve(JSON.parse(xhr.responseText));
          } catch {
            reject(new Error('Invalid response'));
          }
        } else {
          try {
            const err = JSON.parse(xhr.responseText);
            reject(new Error(err.error || `Upload failed (${xhr.status})`));
          } catch {
            reject(new Error(`Upload failed (${xhr.status})`));
          }
        }
      });

      xhr.addEventListener('error', () => reject(new Error('Network error')));
      xhr.addEventListener('abort', () => reject(new Error('Upload cancelled')));

      xhr.open('POST', `${BASE}/books/${slug}/upload`);
      xhr.send(formData);
    });
  },
```

Key decisions:
- Uses `XMLHttpRequest` instead of `fetch` because `fetch` doesn't support upload progress events
- Progress callback receives `0..100` percentage
- Error parsing handles both JSON error responses and HTTP status codes

### Step 3: Add the import

At the top of `api.ts`, find:

```typescript
import type { BookSummary, BookDetail, BookContent } from './types';
```

Replace with:

```typescript
import type { BookSummary, BookDetail, BookContent, UploadResult } from './types';
```

### Step 4: Commit

```bash
git add frontend/src/lib/api.ts frontend/src/lib/types.ts
git commit -m "feat(frontend): add upload API method with progress tracking"
```

---

## Task 3: UploadZone Component

**Dependencies:** None (can run in parallel with Tasks 1–2)
**Files:**
- Create: `frontend/src/lib/components/UploadZone.svelte`

### Step 1: Create `UploadZone.svelte`

Create `frontend/src/lib/components/UploadZone.svelte`:

```svelte
<script lang="ts">
  import { api } from '$lib/api';

  let { slug, onUploaded }: {
    slug: string;
    onUploaded: (result: { sourceFile: string; status: string }) => void;
  } = $props();

  let dragging = $state(false);
  let uploading = $state(false);
  let progress = $state(0);
  let uploadError = $state('');
  let selectedFile: File | null = $state(null);

  const ACCEPTED = '.epub,.pdf,.docx,.doc,.txt,.html,.htm,.pptx,.xlsx,.xls,.csv,.ipynb,.md';

  function handleFile(file: File) {
    const ext = file.name.lastIndexOf('.') >= 0
      ? file.name.slice(file.name.lastIndexOf('.')).toLowerCase()
      : '';
    const allowed = ACCEPTED.split(',').map(e => e.trim());
    if (!allowed.includes(ext)) {
      uploadError = `Unsupported file type: ${ext || 'none'}. Accepted: ${ACCEPTED}`;
      return;
    }
    selectedFile = file;
    uploadError = '';
  }

  function handleDrop(e: DragEvent) {
    e.preventDefault();
    dragging = false;
    const file = e.dataTransfer?.files[0];
    if (file) handleFile(file);
  }

  function handleDragOver(e: DragEvent) {
    e.preventDefault();
    dragging = true;
  }

  function handleDragLeave() {
    dragging = false;
  }

  function handleInputChange(e: Event) {
    const target = e.target as HTMLInputElement;
    const file = target.files?.[0];
    if (file) handleFile(file);
  }

  async function upload() {
    if (!selectedFile) return;

    uploading = true;
    progress = 0;
    uploadError = '';

    try {
      const result = await api.upload(slug, selectedFile, (pct) => {
        progress = pct;
      });
      onUploaded(result);
    } catch (err: any) {
      uploadError = err.message || 'Upload failed';
      uploading = false;
    }
  }

  function clear() {
    selectedFile = null;
    uploadError = '';
    progress = 0;
  }
</script>

<div class="upload-zone">
  {#if !uploading}
    <!-- Drop zone / file selector -->
    <div
      class="drop-area"
      class:dragging
      ondrop={handleDrop}
      ondragover={handleDragOver}
      ondragleave={handleDragLeave}
      role="button"
      tabindex="0"
    >
      {#if selectedFile}
        <div class="file-preview">
          <span class="file-icon">📄</span>
          <span class="file-name">{selectedFile.name}</span>
          <span class="file-size">({(selectedFile.size / 1024 / 1024).toFixed(1)} MB)</span>
          <button class="btn-clear" onclick={clear} title="Remove file">✕</button>
        </div>
        <button class="btn-upload" onclick={upload}>
          Upload & Convert
        </button>
      {:else}
        <p class="drop-hint">Drag & drop a file here, or click to browse</p>
        <p class="drop-formats">EPUB, PDF, DOCX, TXT, HTML, and more</p>
      {/if}

      <input
        type="file"
        accept={ACCEPTED}
        onchange={handleInputChange}
        class="file-input"
      />
    </div>
  {:else}
    <!-- Upload progress -->
    <div class="progress-area">
      <div class="progress-label">
        Uploading... {progress}%
      </div>
      <div class="progress-bar">
        <div class="progress-fill" style="width: {progress}%"></div>
      </div>
    </div>
  {/if}

  {#if uploadError}
    <div class="upload-error">{uploadError}</div>
  {/if}
</div>

<style>
  .upload-zone {
    width: 100%;
    max-width: 600px;
    margin: 0 auto;
  }

  .drop-area {
    border: 2px dashed var(--border);
    border-radius: 12px;
    padding: 40px 24px;
    text-align: center;
    cursor: pointer;
    transition: border-color 0.2s, background-color 0.2s;
    position: relative;
  }

  .drop-area:hover,
  .drop-area.dragging {
    border-color: var(--accent);
    background: rgba(99, 102, 241, 0.05);
  }

  .drop-hint {
    color: var(--text-primary);
    font-size: 15px;
    margin-bottom: 8px;
  }

  .drop-formats {
    color: var(--text-secondary);
    font-size: 12px;
  }

  .file-input {
    position: absolute;
    inset: 0;
    opacity: 0;
    cursor: pointer;
  }

  .file-preview {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    margin-bottom: 16px;
    flex-wrap: wrap;
  }

  .file-icon {
    font-size: 20px;
  }

  .file-name {
    font-weight: 600;
    font-size: 14px;
    color: var(--text-primary);
    word-break: break-all;
  }

  .file-size {
    color: var(--text-secondary);
    font-size: 12px;
  }

  .btn-clear {
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 14px;
    padding: 2px 6px;
    border-radius: 4px;
  }

  .btn-clear:hover {
    color: #f97583;
  }

  .btn-upload {
    background: var(--accent);
    color: white;
    border: none;
    padding: 10px 24px;
    border-radius: 8px;
    font-size: 14px;
    font-weight: 600;
    cursor: pointer;
    transition: opacity 0.2s;
  }

  .btn-upload:hover {
    opacity: 0.9;
  }

  .progress-area {
    padding: 24px;
  }

  .progress-label {
    font-size: 14px;
    color: var(--text-primary);
    margin-bottom: 8px;
  }

  .progress-bar {
    height: 8px;
    background: var(--bg-tertiary);
    border-radius: 4px;
    overflow: hidden;
  }

  .progress-fill {
    height: 100%;
    background: var(--accent);
    border-radius: 4px;
    transition: width 0.3s ease;
  }

  .upload-error {
    margin-top: 12px;
    padding: 8px 12px;
    background: #3d1f1f;
    color: #f97583;
    border-radius: 6px;
    font-size: 12px;
  }
</style>
```

Key design:
- **Click-to-browse OR drag-and-drop** — the `<input type="file">` is overlaid on the drop zone
- **Two-step flow**: select file → see preview → click "Upload & Convert"
- **Extension validation** on the frontend before sending (mirrors backend whitelist)
- **Progress bar** during upload using XHR progress events
- **Error display** for rejected file types or failed uploads
- **`onUploaded` callback** — parent component handles post-upload state transition

### Step 2: Commit

```bash
git add frontend/src/lib/components/UploadZone.svelte
git commit -m "feat(frontend): add UploadZone component with drag-drop and progress bar"
```

---

## Task 4: Integrate Upload Into Book Detail Page

**Dependencies:** Tasks 1, 2, 3
**Files:**
- Modify: `frontend/src/routes/books/[slug]/+page.svelte`

### Step 1: Add UploadZone import and integration

In `frontend/src/routes/books/[slug]/+page.svelte`:

**a) Add import.** Find:

```typescript
  import BookEditor from '$lib/components/BookEditor.svelte';
```

Insert after:

```typescript
  import UploadZone from '$lib/components/UploadZone.svelte';
```

**b) Add `handleUploaded` callback.** Find:

```typescript
  let showLore = $state(false);
```

Insert after:

```typescript

  function handleUploaded(result: { sourceFile: string; status: string }) {
    // Refresh book data to pick up new status + sourceFile
    loadBook();
  }
```

**c) Replace the status display in the main content area.** Find the block that shows status messages when the book isn't ready:

```svelte
          {:else}
            <div class="status-message">
```

This is the entire `{:else}` block after the `{#if book?.status === 'ready'}` section that shows the editor. Replace everything from `{:else}` through its closing `{/if}` with:

```svelte
          {:else if book?.status === 'pending'}
            <div class="status-section">
              <p class="status-message">Upload a file to convert to markdown.</p>
              <UploadZone {slug} onUploaded={handleUploaded} />
            </div>
          {:else if book?.status === 'converting'}
            <div class="status-section">
              <p class="status-message converting">Converting to markdown...</p>
              <div class="spinner"></div>
              <button class="btn-secondary" onclick={loadBook}>Refresh</button>
            </div>
          {:else}
            <div class="status-section">
              <p class="status-message error">
                Conversion failed{book?.errorMessage ? `: ${book.errorMessage}` : ''}
              </p>
              <UploadZone {slug} onUploaded={handleUploaded} />
            </div>
          {/if}
```

**d) Add styles.** Before the closing `</style>`, add:

```css
  .status-section {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 16px;
    padding: 40px 24px;
  }

  .status-message {
    color: var(--text-secondary);
    font-size: 14px;
  }

  .status-message.converting {
    color: var(--accent);
  }

  .status-message.error {
    color: #f97583;
  }

  .spinner {
    width: 24px;
    height: 24px;
    border: 3px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  .btn-secondary {
    padding: 6px 16px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    cursor: pointer;
    font-size: 13px;
  }

  .btn-secondary:hover {
    opacity: 0.8;
  }
```

**e) Remove old conversion trigger.** The existing `triggerConversion()` function and its "Convert" button are no longer needed (upload auto-triggers conversion). Find and remove:

```typescript
  async function triggerConversion() {
    if (!slug) return;
    try {
      await api.triggerConversion(slug);
      // Poll for status changes
      const poll = setInterval(async () => {
        const updated = await api.getBook(slug);
        if (updated) {
          book = updated;
          if (updated.status === 'ready') {
            clearInterval(poll);
            loadBook();
          } else if (updated.status === 'error') {
            clearInterval(poll);
          }
        }
      }, 3000);
    } catch (err) {
      console.error('Conversion failed:', err);
    }
  }
```

Also find and remove the toolbar button that calls it. Look for a `Convert` or conversion-trigger button in the toolbar area. It may look like:

```svelte
            <button class="btn" onclick={triggerConversion}>Convert</button>
```

Remove this button. If it's inside a conditional block like `{#if book?.status === 'pending'}`, remove the entire conditional block.

**f) Add conversion status polling after upload.** The `handleUploaded` callback should also start polling. Replace:

```typescript
  function handleUploaded(result: { sourceFile: string; status: string }) {
    // Refresh book data to pick up new status + sourceFile
    loadBook();
  }
```

With:

```typescript
  function handleUploaded(result: { sourceFile: string; status: string }) {
    loadBook();
    // Poll for conversion completion
    const poll = setInterval(async () => {
      if (!slug) { clearInterval(poll); return; }
      try {
        const updated = await api.getBook(slug);
        if (updated) {
          book = updated;
          if (updated.status === 'ready') {
            clearInterval(poll);
            await loadBook(); // Loads content too
          } else if (updated.status === 'error') {
            clearInterval(poll);
          }
        }
      } catch {
        clearInterval(poll);
      }
    }, 3000);
  }
```

### Step 2: Build and verify

```bash
cd J:/workspace2/llm/continue_story_4/frontend
npx svelte-check --tsconfig ./tsconfig.json 2>&1 | tail -5
```

Expected: No new errors (pre-existing type warnings are acceptable).

### Step 3: Commit

```bash
git add frontend/src/routes/books/
git commit -m "feat(frontend): integrate UploadZone into book detail page with conversion polling"
```

---

## Task 5: End-to-End Verification

**Dependencies:** Tasks 1–4
**Files:** None (read-only verification)

### Step 1: Build all containers

```bash
cd J:/workspace2/llm/continue_story_4
docker compose down -v 2>/dev/null || true
docker compose build 2>&1
```

Expected: All 3 services build successfully.

### Step 2: Start the stack

```bash
docker compose up -d 2>&1
```

### Step 3: Wait for health

```bash
sleep 15
curl -f http://localhost:5000/api/health
```

Expected: `{"status":"healthy",...}`

### Step 4: Test the upload flow end-to-end

```bash
# 1. Create a book
echo "=== Create book ==="
curl -s -X POST http://localhost:5000/api/books \
  -H "Content-Type: application/json" \
  -d '{"title":"Upload Test","author":"Test Author"}'

# 2. Create a test EPUB-like file and upload it
echo ""
echo "=== Create test file ==="
echo "This is a test book content." > /tmp/test-upload.txt

# 3. Upload the file
echo ""
echo "=== Upload file ==="
curl -s -w "\nHTTP Status: %{http_code}" -X POST \
  http://localhost:5000/api/books/upload-test/upload \
  -F "file=@/tmp/test-upload.txt"

# 4. Check status (should be converting)
echo ""
echo "=== Status ==="
curl -s http://localhost:5000/api/books/upload-test

# 5. Wait for conversion and check again
echo ""
echo "=== Wait for conversion ==="
sleep 10
curl -s http://localhost:5000/api/books/upload-test

# 6. Check content was generated
echo ""
echo "=== Content ==="
curl -s http://localhost:5000/api/books/upload-test/content

# 7. Test invalid extension
echo ""
echo "=== Invalid extension ==="
echo "bad" > /tmp/test.exe
curl -s -w "\nHTTP Status: %{http_code}" -X POST \
  http://localhost:5000/api/books/upload-test/upload \
  -F "file=@/tmp/test.exe"

# 8. Test non-existent book
echo ""
echo "=== Non-existent book ==="
echo "test" > /tmp/test.txt
curl -s -w "\nHTTP Status: %{http_code}" -X POST \
  http://localhost:5000/api/books/nonexistent/upload \
  -F "file=@/tmp/test.txt"

# 9. Verify frontend loads
echo ""
echo "=== Frontend ==="
curl -f http://localhost:5173 | head -3
```

Expected:
- Create returns 201
- Upload returns 200 with `{ status: "converting", jobId: "..." }`
- Status transitions: `pending` → `converting` → `ready`
- Content endpoint returns the converted markdown
- Invalid extension returns 400
- Non-existent book returns 404
- Frontend loads

### Step 5: Clean up

```bash
docker compose down -v
```

### Step 6: Verify no pending changes

```bash
git status --short
```

Expected: No uncommitted changes.
