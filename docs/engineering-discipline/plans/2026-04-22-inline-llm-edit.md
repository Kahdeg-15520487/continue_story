# Inline LLM-Assisted Editing — Implementation Plan

## Overview

**Killer feature**: Select text in the editor → floating menu appears → user types a custom instruction → agent writes a scratch file → frontend shows inline diff (scratch vs original) → user accepts or rejects.

**Flow**:
```
User selects text → "Ask AI" button floats near selection
  → User types instruction (e.g. "make this more dramatic")
  → Backend: create write-mode session, send prompt with selected text + instruction + chapter context
  → Agent writes full chapter to scratch file: chapters/ch-001.scratch.md
  → Backend streams completion event
  → Frontend: fetch scratch file content, compute diff, show inline diff overlay
  → User clicks "Accept" → scratch content replaces original chapter → delete scratch file
  → User clicks "Reject" → delete scratch file, restore original
```

## Architecture

```
┌─────────────────────────────────────────────────┐
│ BookEditor.svelte                                │
│   └─ detects text selection                      │
│   └─ shows <FloatingEditMenu> at selection       │
│       └─ user types instruction                  │
│       └─ calls api.inlineEdit(slug, chId, ...)   │
│                                                   │
│   └─ receives streamed result                     │
│   └─ shows <DiffOverlay> in editor               │
│       └─ green = additions, red = deletions      │
│       └─ Accept → save scratch as chapter        │
│       └─ Reject → reload original                 │
└─────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────┐
│ POST /api/books/{slug}/chapters/{id}/inline-edit │
│   SSE stream                                      │
│   Agent writes to: chapters/ch-NNN.scratch.md    │
│   Final event: { type: "edit_done", path: ... }  │
└─────────────────────────────────────────────────┘
         │
         ▼
┌─────────────────────────────────────────────────┐
│ Agent (write mode)                                │
│   Receives: chapter content, selected text,       │
│             instruction, chapter context          │
│   Writes: full rewritten chapter to scratch file  │
└─────────────────────────────────────────────────┘
```

---

## Task 1: Backend — Inline Edit Endpoint

**File**: `backend/src/KnowledgeEngine.Api/Endpoints/InlineEditEndpoints.cs` (new)

**Endpoint**: `POST /api/books/{slug}/chapters/{id}/inline-edit`

**Request body**:
```json
{
  "selectedText": "the exact text the user selected",
  "instruction": "make this more dramatic",
  "selectionStart": 145,
  "selectionEnd": 230
}
```

**Validation**:
- `selectedText` must be non-empty
- `instruction` must be non-empty
- `slug` must not contain `..`, `/`, `\`
- Chapter file must exist

**Logic**:
1. Read the full chapter file from `{Library:Path}/{slug}/chapters/{id}.md`
2. Verify `selectedText` actually appears in the chapter content (case-sensitive match)
3. Build a prompt for the agent:
   ```
   You are editing a chapter of a book. The user has selected some text and given an instruction.

   ## Chapter: {chapterTitle}
   {full chapter content}

   ## Selected Text (lines {startLine}-{endLine})
   ```
   {selectedText}
   ```

   ## Instruction
   {instruction}

   Rewrite the ENTIRE chapter incorporating the requested change. Keep everything else the same unless the instruction requires broader changes. Output the complete rewritten chapter as markdown.

   Write the result to: chapters/{id}.scratch.md
   ```
4. Call `agentService.EnsureSessionAsync(slug, "write")` — use a session key scoped to the chapter+edit, e.g. `{slug}-edit-{chapterId}` to avoid conflicting with other sessions
5. Stream the response via `agentService.StreamPromptAsync(sessionId, prompt)`
6. Forward SSE events to the client (same pattern as ChatEndpoints)
7. After stream completes, check if scratch file exists
8. Send final event: `data: {"type":"edit_done","scratchPath":"chapters/{id}.scratch.md","exists":true/false}`
9. Kill the session (write sessions are one-shot)

**SSE event types sent to client**:
- Forwarded agent events: `message_update` (text_delta, thinking_delta)
- `edit_done`: `{ scratchPath: string, exists: boolean }`
- `error`: `{ message: string }`

**Register in Program.cs**: `InlineEditEndpoints.Map(app);`

---

## Task 2: Backend — Accept/Reject Endpoints

**File**: `backend/src/KnowledgeEngine.Api/Endpoints/InlineEditEndpoints.cs` (same file)

**Accept endpoint**: `POST /api/books/{slug}/chapters/{id}/inline-edit/accept`

**Logic**:
1. Read scratch file `{Library:Path}/{slug}/chapters/{id}.scratch.md`
2. Verify it exists, return 404 if not
3. Overwrite original chapter file with scratch content
4. Delete scratch file
5. Return `{ accepted: true }`

**Reject endpoint**: `POST /api/books/{slug}/chapters/{id}/inline-edit/reject`

**Logic**:
1. Delete scratch file if it exists
2. Return `{ rejected: true }`

**Cleanup on reject**: No-op if scratch doesn't exist (idempotent).

---

## Task 3: Backend — Scratch File Cleanup

**File**: `backend/src/KnowledgeEngine.Api/Services/ScratchCleanupService.cs` (new)

**Background service** (hosted service, runs on startup):
- On startup, scan all book directories for `*.scratch.md` files in `chapters/`
- Delete any scratch files older than 1 hour (stale from abandoned edits)
- Runs once on startup only (not a periodic timer — just cleans up on boot)

**Register**: `builder.Services.AddHostedService<ScratchCleanupService>();`

---

## Task 4: Frontend — Selection Detection & Floating Menu

**File**: `frontend/src/lib/components/InlineEditMenu.svelte` (new)

**Props**:
```ts
{
  bookSlug: string;
  chapterId: string;
  visible: boolean;
  position: { top: number; left: number };
  selectedText: string;
  onEditStart: () => void;
  onEditStream: (delta: string) => void;
  onEditDone: (scratchPath: string) => void;
  onEditError: (message: string) => void;
}
```

**UI**:
- Small floating card with:
  - Text input for instruction (auto-focused when visible)
  - "Send" button (or Enter key)
  - Loading spinner while streaming
  - Cancel button (AbortController)
- Positioned absolutely near the text selection
- Click outside dismisses

**Behavior**:
1. When user clicks "Send", call `api.inlineEdit(bookSlug, chapterId, selectedText, instruction, callbacks)`
2. Show streaming status (spinner + "AI is editing...")
3. On done, call `onEditDone(scratchPath)`
4. On error, show error message
5. Cancel button aborts the SSE stream

---

## Task 5: Frontend — Diff Overlay Component

**File**: `frontend/src/lib/components/DiffOverlay.svelte` (new)

**Props**:
```ts
{
  originalContent: string;
  scratchContent: string;
  onAccept: () => void;
  onReject: () => void;
}
```

**UI**:
- Replaces the normal editor content area (overlays on top of BookEditor)
- Shows a diff view with:
  - Green highlighted lines = additions
  - Red highlighted lines = deletions
  - Unchanged lines shown normally
- Bottom bar with two buttons: "✓ Accept" (green) and "✗ Reject" (red)
- "Accept" calls `onAccept()`, "Reject" calls `onReject()`

**Diff algorithm**:
- Use a simple line-based diff (no external dependency needed)
- Implement a basic LCS-based diff or use a lightweight approach:
  - Split both texts into lines
  - Compare line by line
  - Mark lines as: unchanged, added, deleted
- Alternative: use the `diff` package from npm (`diff` library, ~30KB) — `npm install diff @types/diff`
  - `import { diffLines } from 'diff'`
  - Produces `{ value, added, removed }` chunks — perfect for rendering

**Rendering**:
```svelte
{#each diffChunks as chunk}
  {#if chunk.added}
    <div class="diff-line added">{chunk.value}</div>
  {:else if chunk.removed}
    <div class="diff-line removed">{chunk.value}</div>
  {:else}
    <div class="diff-line">{chunk.value}</div>
  {/if}
{/each}
```

---

## Task 6: Frontend — API Client Method

**File**: `frontend/src/lib/api.ts` (modify)

Add `inlineEdit` method:

```ts
inlineEdit(
  bookSlug: string,
  chapterId: string,
  selectedText: string,
  instruction: string,
  onChunk: (delta: string) => void,
  onDone: (scratchPath: string) => void,
  onError?: (err: string) => void,
  onThinking?: (text: string) => void,
): AbortController
```

- Same SSE pattern as `chat()` method
- POST to `/api/books/{slug}/chapters/{id}/inline-edit`
- Body: `{ selectedText, instruction }`
- Parse SSE events, forward to callbacks
- On `edit_done` event, extract `scratchPath` and call `onDone(scratchPath)`

---

## Task 7: Frontend — BookEditor Selection API

**File**: `frontend/src/lib/components/BookEditor.svelte` (modify)

Expose selection API to parent:

1. Add callback prop: `onTextSelect?: (text: string, range: { from: number; to: number }, coords: { top: number; left: number }) => void`
2. Add a ProseMirror plugin (or use `listener`) to detect selection changes:
   ```ts
   // In the listener setup after editor creation:
   editorView.dom.addEventListener('mouseup', () => {
     const { state } = editorView;
     const { from, to } = state.selection;
     const text = state.doc.textBetween(from, to);
     if (text.trim()) {
       // Get screen coordinates of selection
       const start = editorView.coordsAtPos(from);
       const end = editorView.coordsAtPos(to);
       onTextSelect?.(text, { from, to }, {
         top: start.top - 40, // Position above selection
         left: (start.left + end.left) / 2,
       });
     } else {
       onTextSelect?.('', { from: 0, to: 0 }, { top: 0, left: 0 });
     }
   });
   ```
3. Also handle `keyup` for keyboard selection (Shift+Arrow keys)

---

## Task 8: Frontend — Wire It All Together in Book Page

**File**: `frontend/src/routes/books/[slug]/+page.svelte` (modify)

Add state and orchestration:

```ts
// New state
let selectedText = $state('');
let selectionCoords = $state({ top: 0, left: 0 });
let selectionRange = $state({ from: 0, to: 0 });
let showInlineEdit = $state(false);
let isAiEditing = $state(false);
let diffState = $state<{ original: string; scratch: string } | null>(null);
```

**Flow**:
1. `BookEditor` calls `onTextSelect` → set `selectedText`, `selectionCoords`, `showInlineEdit = true`
2. `InlineEditMenu` visible → user types instruction → calls API
3. On `onEditDone(scratchPath)`:
   - Fetch scratch content: `api.getChapter(slug, scratchPath)` (works if we add a GET endpoint for scratch files, or use a dedicated endpoint)
   - Actually, scratch files are in the same `chapters/` dir — we can add a `GET /api/books/{slug}/chapters/{id}/scratch` endpoint, or simpler: just read the scratch content from the `edit_done` event
   - **Better approach**: have the backend return the scratch content in the `edit_done` event (or a separate fetch). Simplest: fetch via `GET /api/books/{slug}/content?file=chapters/{id}.scratch.md` — but that doesn't exist. Let's add a dedicated endpoint.

**Backend addition**: `GET /api/books/{slug}/chapters/{id}/scratch` returns `{ content: string }` or 404.

4. Set `diffState = { original: content, scratch: scratchContent }`
5. User accepts → call `api.acceptInlineEdit(slug, chapterId)` → reload chapter content → clear diffState
6. User rejects → call `api.rejectInlineEdit(slug, chapterId)` → clear diffState

**API additions for accept/reject**:
```ts
acceptInlineEdit: (slug: string, id: string) =>
  request<{ accepted: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/inline-edit/accept`, { method: 'POST' }),

rejectInlineEdit: (slug: string, id: string) =>
  request<{ rejected: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/inline-edit/reject`, { method: 'POST' }),

getScratchContent: (slug: string, id: string) =>
  request<{ content: string }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/scratch`),
```

---

## Task 9: Styling & Polish

**Files**: Various Svelte component `<style>` blocks

**Floating menu styling**:
- Small card with shadow, rounded corners
- Z-index above editor content
- Subtle animation (fade in)
- Fixed position relative to viewport

**Diff overlay styling**:
- Full editor area replacement
- Added lines: green background (`rgba(46, 160, 67, 0.15)`)
- Removed lines: red background (`rgba(248, 81, 73, 0.15)`)
- Left border color indicator (2px green/red)
- Bottom action bar: sticky, with clear accept/reject buttons
- Scrollable if diff is long

**Editor selection highlight**:
- When AI edit menu is visible, keep the selected text highlighted (blue background)
- When diff overlay is shown, hide the editor underneath

---

## Task 10: Edge Cases & Error Handling

**Frontend**:
- No text selected: don't show menu
- Empty instruction: disable Send button
- Network error during streaming: show error, allow retry
- User navigates away during edit: abort the stream
- User switches chapters during edit: abort and reject scratch
- Multiple rapid selections: debounce, only show menu for latest

**Backend**:
- Selected text not found in chapter: return error before calling agent
- Agent fails to write scratch file: send `edit_done` with `exists: false`
- Scratch file missing on accept: return 404
- Concurrent edits on same chapter: last writer wins (acceptable for single-user app)
- Session cleanup: kill write session after edit completes (already in Task 1)

---

## File Summary

### New files (5):
| File | Purpose |
|---|---|
| `backend/src/.../Endpoints/InlineEditEndpoints.cs` | Inline edit SSE + accept/reject + get scratch |
| `backend/src/.../Services/ScratchCleanupService.cs` | Startup cleanup of stale scratch files |
| `frontend/src/lib/components/InlineEditMenu.svelte` | Floating menu with instruction input |
| `frontend/src/lib/components/DiffOverlay.svelte` | Inline diff viewer with accept/reject |
| `frontend/src/lib/diff.ts` | Line-based diff utility (or use `diff` npm package) |

### Modified files (3):
| File | Changes |
|---|---|
| `frontend/src/lib/components/BookEditor.svelte` | Add `onTextSelect` prop, mouseup/keyup listener |
| `frontend/src/lib/api.ts` | Add `inlineEdit`, `acceptInlineEdit`, `rejectInlineEdit`, `getScratchContent` |
| `frontend/src/routes/books/[slug]/+page.svelte` | Wire up selection → menu → diff → accept/reject flow |
| `backend/src/KnowledgeEngine.Api/Program.cs` | Register `InlineEditEndpoints.Map(app)`, `ScratchCleanupService` |

### Dependencies:
- Frontend: `npm install diff @types/diff` (lightweight diff library, ~30KB)

---

## Execution Order

```
Task 1: Backend inline edit endpoint (SSE streaming)
Task 2: Backend accept/reject endpoints
Task 3: Backend scratch cleanup service
Task 4: Frontend API client methods
Task 5: Frontend BookEditor selection API
Task 6: Frontend InlineEditMenu component
Task 7: Frontend DiffOverlay component (install diff lib)
Task 8: Wire everything in book page
Task 9: Styling & polish
Task 10: Edge cases & error handling
```

Tasks 1-3 (backend) and Task 7 (diff lib install) can run in parallel.
Tasks 4-6 depend on their respective backend/frontend counterparts.
Task 8 depends on all others.
Task 9-10 are polish passes after integration.
