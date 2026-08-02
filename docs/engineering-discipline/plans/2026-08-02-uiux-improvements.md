# UI/UX Improvements — Implementation Plan

> **Worker note:** Execute this plan task-by-task. Each task uses checkbox (`- [ ]`) syntax for progress tracking. Source of truth for findings: `docs/engineering-discipline/plans/2026-08-02-uiux-sweep-findings.md` (finding IDs referenced as `1.1`, `2.3`, etc.).

**Goal:** Fix the data-loss/correctness bugs and UX gaps identified in the UI/UX sweep — a toast/error system, save-integrity guarantees, chat correctness fixes, interaction polish, accessibility, and small-feature improvements — across the Svelte 5 frontend and the ASP.NET API where behavior originates.

**Architecture:** One new cross-cutting primitive (toast store + component mounted in the root layout) that all mutations report through. Save-integrity work lives in the book page + BookEditor. Chat fixes live in ChatPanel + api.ts + one backend endpoint tweak (sessionId persistence). Feature polish is component-local. Backend changes: 3 small ones (wiki entity PUT, per-book conversion job id + status, chat POST sessionId, hangfire-link dev flag).

**Tech Stack:** Svelte 5 (frontend), ASP.NET Core 8 minimal APIs + EF Core SQLite (backend), marked + diff (existing deps), DOMPurify (new dep).

**Work Scope:**
- **In scope:**
  - Toast/notification store + component; all silent `console.error` catches surface through it
  - Editor save integrity: flush pending debounce on chapter switch / lock, unsaved-changes indicator, `beforeunload` guard
  - ChatPanel: retry dedup, KB stop via server abort, session-filtered history, stick-to-bottom scroll, render debounce, partial-response preservation
  - Markdown sanitization + external-link targets (shared render helper)
  - Panel persistence, toolbar active states, delete affordances, keyboard fixes, a11y basics
  - Wiki panel edit capability (new backend PUT endpoint), per-book conversion stats (new DB column + migration)
  - Small polish items from the sweep (upload UX, searches, tooltips, sort persistence, etc.)
- **Out of scope:**
  - Responsive/mobile redesign (app is desktop-first by design; noted as separate effort)
  - Full keyboard-shortcut suite, command palette, theming engine
  - List virtualization for very long chapters/diffs
  - Authentication, multi-user, i18n

**Verification Strategy:**
- **Level:** build + integration tests + manual
- **Commands:**
  - Backend: `cd backend && dotnet build --no-restore && dotnet test`
  - Frontend: `cd frontend && npm run build` (and `npm run check` when available)
  - Stack: `docker compose build && docker compose up -d`
- **What it validates:** compiles clean, existing integration tests pass, and each task's manual checklist passes in the running stack.

---

## File Structure Mapping

### New files
| File | Responsibility |
|---|---|
| `frontend/src/lib/toast.svelte.ts` | Toast store (Svelte 5 runes): `toasts` state, `toast.error/success/info` |
| `frontend/src/lib/components/Toasts.svelte` | Toast renderer (fixed stack, auto-dismiss, aria-live) |
| `frontend/src/lib/markdown.ts` | Shared `renderMarkdown()` — marked + DOMPurify + link targets (used by ChatPanel, LorePanel, KnowledgePanel) |

### Modified files
| File | Change |
|---|---|
| `frontend/src/routes/+layout.svelte` | Mount `<Toasts />` |
| `frontend/src/routes/+page.svelte` | Error state + retry, toast on failures, empty-book name prompt, error pill wrap |
| `frontend/src/routes/books/[slug]/+page.svelte` | Save flush, unsaved indicator, retry button, panel persistence, Escape close, active toggle states, stale-menu fix, wiki refresh hook |
| `frontend/src/lib/components/BookEditor.svelte` | Readonly selection, wrapper `bind:this`, Ctrl+S |
| `frontend/src/lib/components/ChatPanel.svelte` | Retry dedup, KB abort, session-filtered history, stick-to-bottom, render debounce, timestamps/copy/autosize, hide KB retry |
| `frontend/src/lib/components/ChapterSidebar.svelte` | Delete affordances, regenerate-title progress, search + tooltips, toast errors |
| `frontend/src/lib/components/LorePanel.svelte` | Error surfacing, refresh, edit mode, search |
| `frontend/src/lib/components/KnowledgePanel.svelte` | Search debounce, toast errors, dead-code removal, edit-loss guard |
| `frontend/src/lib/components/BookList.svelte` | Delete affordances, error tooltip, sort persistence, toast errors |
| `frontend/src/lib/components/UploadZone.svelte` | Keyboard fix, size check, error wrap |
| `frontend/src/lib/components/DiffOverlay.svelte` | Accept/Reject busy state |
| `frontend/src/lib/components/InlineEditMenu.svelte` | Initial-position clamp |
| `frontend/src/lib/api.ts` | `saveWikiEntity`, `saveChatMessage` sessionId, KB chat abort wiring, `ConversionStatus` shape |
| `frontend/src/routes/knowledge/+page.svelte` | Resizable chat pane |
| `frontend/src/app.css` | Global `:focus-visible` styles, link styles |
| `backend/.../Models/Book.cs` | `ConversionJobId` column |
| `backend/.../Migrations/` | New migration for `ConversionJobId` |
| `backend/.../Endpoints/UploadEndpoints.cs` | Persist job id; per-book status |
| `backend/.../Endpoints/ChatHistoryEndpoints.cs` | Accept + store `SessionId` on POST |
| `backend/.../Endpoints/LoreEndpoints.cs` | PUT wiki entity endpoint |
| `frontend/package.json` | Add `dompurify` |

---

## Phase A — Foundations

### Task 1: Toast / notification system

**Dependencies:** None

The single highest-leverage change — every silent `console.error` in the app reports through this.

- [x] Create `frontend/src/lib/toast.svelte.ts`:
  ```typescript
  import { type Snippet } from 'svelte';
  export type ToastKind = 'error' | 'success' | 'info';
  export interface Toast { id: number; kind: ToastKind; message: string; }
  let toasts: Toast[] = $state([]);
  let nextId = 1;
  export function toast(kind: ToastKind, message: string, duration = 4000) {
    const id = nextId++;
    toasts.push({ id, kind, message });
    if (duration > 0) setTimeout(() => dismiss(id), duration);
    return id;
  }
  export const toastError = (m: string) => toast('error', m);
  export const toastSuccess = (m: string) => toast('success', m);
  export function dismiss(id: number) { toasts = toasts.filter(t => t.id !== id); }
  ```
- [x] Create `frontend/src/lib/components/Toasts.svelte`: fixed bottom-right stack, kind-based colors (`--error`/`--success`/accent), dismiss button, `role="status"` + `aria-live="polite"` container, fade/slide transition.
- [x] Mount `<Toasts />` in `frontend/src/routes/+layout.svelte` inside `.app-layout`.
- [x] Verify: trigger a test toast from the library page; it appears, auto-dismisses, and is announced politely.

---

## Phase B — Data-loss & correctness fixes

### Task 2: Editor save integrity — flush on switch/lock, unsaved indicator, beforeunload

**Dependencies:** None (frontend only)

Fixes `1.1` (silent edit loss) and `2.14` (no unsaved-changes awareness).

- [x] In `books/[slug]/+page.svelte` track `lastSavedContent` (set in `saveContent` success and after `loadChapterContent`/`handleChapterSelect`).
- [x] Extract `flushPendingSave(): Promise<void>` — clears `saveTimeout`, and if `content !== lastSavedContent`, awaits `saveContent(content)` **before** any guard can skip it.
- [x] `handleChapterSelect`: `await flushPendingSave()` before loading the target chapter (replaces the current "cancel pending save" that discards edits).
- [x] Lock button handler: `await flushPendingSave()` **before** toggling `isEditing` (fixes `saveContent`'s `!isEditing` early-return dropping the last edits).
- [x] Add `onbeforeunload` on the book page (`svelte:window`): if `content !== lastSavedContent` or `saveTimeout` pending → `e.preventDefault()` (browser shows the native confirmation).
- [x] Toolbar "Saving…" indicator becomes a three-state label: `Unsaved changes` (dirty, amber) / `Saving…` / `Saved` (muted, fades after 2s).
- [x] Verify: type 3 words, click another chapter within 1s → content saved, no loss; type, close tab → browser warns; lock → nothing lost.

### Task 3: Error surfacing — all mutations report through toasts; load-error states with retry

**Dependencies:** Task 1

Fixes `1.2`, `1.8`, `2.3`.

- [x] Book page: `saveContent`, `saveTitle`, `handleAcceptEdit`, `handleChatEditDone`, `handleEditDone` failures → `toastError(...)` instead of `console.error`.
- [x] Library page `+page.svelte`: `loadBooks()` failure → error banner above `BookList` with **Retry** button (no more misleading "No books yet" empty state); `createBook`/`uploadFile` failures → `toastError`.
- [x] Book page `loadBook()` failure screen: add **Retry** button beside "← Back to Library" that re-runs `loadBook()`.
- [x] `ChapterSidebar`: `addChapter`, `removeChapter`, `regenerateTitles`, `generateWiki` failures → `toastError`.
- [x] `BookList.deleteBook`, `KnowledgePanel.saveEdit/createEntry/deleteEntry/deleteCategory`, `LorePanel` errors → `toastError`.
- [x] Verify: stop the `api` container, attempt each mutation → visible toast; restore container, Retry works on both load-error states.

### Task 4: Chat retry dedup + hide inert KB retry

**Dependencies:** None

Fixes `1.3` (duplicate user bubble on retry) and `2.11` (retry button shown but dead in KB mode).

- [x] In `ChatPanel.svelte` `send()`: remember `const sentMessageIndex = messages.length - 1` (or store the message text) for the in-flight turn.
- [x] `retry()`: instead of appending, **replace** the last user bubble if it equals the retried message text (covers both "last message is user" and "last message is assistant" cases); keep the existing slice-if-assistant behavior as fallback.
- [x] `{#if messages.length > 0}` retry button: only render when `mode === 'book'`.
- [x] Verify: force an error mid-stream, click Retry → exactly one user bubble, one new assistant response.

### Task 5: KB chat "Stop" aborts server-side; preserve partial response on error

**Dependencies:** None

Fixes `1.4` (KB stop is cosmetic; agent keeps running) and the partial-response loss from `3.7`.

- [x] `api.ts`: add `abortKnowledgeChat(sessionId)` → `POST /api/knowledge/chat/abort` (endpoint already exists in `KnowledgeChatEndpoints.cs`).
- [x] `ChatPanel.stop()` knowledge branch: call `await api.abortKnowledgeChat(currentSessionId)` first, then abort the fetch.
- [x] Book + KB `onError` paths: if `currentResponse` is non-empty, append it to `messages` (with `_[Stopped due to error]_` marker) instead of discarding.
- [x] Verify: KB chat, hit Stop mid-research → agent session aborts (no stray KB writes after), partial text preserved with marker; book chat error mid-stream → partial preserved.

### Task 6: Session history isolation

**Dependencies:** None (backend POST change + frontend)

Fixes `1.5` ("New Session" doesn't isolate history — old sessions reappear after reload).

- [x] Backend `ChatHistoryEndpoints.cs` POST: add optional `SessionId` to `SaveChatMessageRequest`; store it on `ChatMessage` (mirrors `ChatEndpoints` which already saves with session id).
- [x] `api.ts` `saveChatMessage`: accept + forward `sessionId`.
- [x] `ChatPanel.onMount`: get/create the session **first**, then `api.getChatHistory(slug, 100, currentSessionId)` (backend GET already supports `?sessionId=`).
- [x] KB mode: pass `currentSessionId` in `saveChatMessage` calls.
- [x] `startNewSession()` already clears the UI list; after reload only the new session's history loads. "Clear history" behavior unchanged.
- [x] Verify: book chat → "+ New Session" → reload → old session's messages do not reappear; KB chat same.

### Task 7: Chat-edit diff no longer pops the stale inline-edit menu

**Dependencies:** None

Fixes `1.6`.

- [x] `books/[slug]/+page.svelte` `handleChatEditDone()`: set `diffState` only — remove `showInlineEdit = true` (the diff renders from `diffState` alone; the floating `InlineEditMenu` stays tied to real text selections).
- [x] Verify: agent edits a chapter via chat → diff overlay appears, no floating "Describe the edit…" box at stale coordinates.

### Task 8: LorePanel error surfacing + refresh on agent edits

**Dependencies:** None

Fixes `1.7` (errors swallowed — user sees "Select an entity to view.") and `2.10` (wiki goes stale after chat edits).

- [x] `LorePanel.selectEntity`/`selectSummary` failures: set `wikiError = err.message` instead of writing into the never-rendered `content` state.
- [x] Book page: on `onResponseDone` (fires after every chat turn), call a `refresh` method on LorePanel (`bind:this` + exported `refresh()` like ChapterSidebar) so wiki entities/summary re-list after agent edits.
- [x] Verify: delete a wiki file on disk → clicking the entity shows a visible error; agent adds a character via chat → panel re-lists without reopening.

---

## Phase C — Interaction & perception

### Task 9: Chat stick-to-bottom scrolling

**Dependencies:** None

Fixes `2.1`.

- [x] `ChatPanel.svelte`: replace the unconditional `scrollTop = scrollHeight` `$effect` with a pinned check — only autoscroll when the container is within ~40px of the bottom (compute `scrollHeight - scrollTop - clientHeight < 40` before scrolling).
- [x] Verify: stream a long response, scroll up mid-stream → position is preserved; scroll back down → autoscroll resumes.

### Task 10: Readonly editor allows text selection

**Dependencies:** None

Fixes `2.2`.

- [x] `BookEditor.svelte`: remove `pointer-events: none` from `.milkdown-wrapper.readonly`; keep `contenteditable="false"` and add `user-select: text` on the wrapper. Selection listeners already tolerate readonly (inline-edit menu is gated by `isEditing`).
- [x] Verify: default (read-only) mode — select and copy prose; clicking still can't modify text.

### Task 11: Per-book conversion stats

**Dependencies:** None (DB migration)

Fixes `2.4` (conversion panel shows global Hangfire counts, misleading with concurrent books).

- [x] `Models/Book.cs`: add `public string? ConversionJobId { get; set; }`.
- [x] New EF migration (`dotnet ef migrations add AddConversionJobId`), apply in `Program.cs` flow as usual.
- [x] `UploadEndpoints.cs`: persist `book.ConversionJobId = jobId` on upload (and clear on re-upload).
- [x] `/status` response: include `jobState` derived from `monitoring.JobDetails(book.ConversionJobId)?.State` (`Succeeded/Processing/Enqueued/Failed/Null`); keep global counts but rename to `systemProcessing/systemQueued/...` so the UI can label them "System-wide".
- [x] Book page conversion panel: show per-book state prominently; keep Hangfire counts as a small secondary line labeled "all jobs".
- [x] Verify: upload two books back-to-back → each book's panel shows its own job state.

### Task 12: Delete affordances + global focus-visible pass

**Dependencies:** None

Fixes `2.5` and part of `3.9`.

- [x] `ChapterSidebar`: delete button `display: none` → always visible at low opacity (opacity transitions on `:hover`/`:focus-within`); keep `tabindex="0"` + Enter handler; add `title="Delete chapter"` (exists).
- [x] `BookList`: delete button `opacity: 0` → `0.35` idle, full on hover/focus; add `:focus-visible` outline.
- [x] `app.css`: global `:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }`.
- [x] Remove `outline: none` from `.title-input` (book page) and KB search input — replace with border-color/focus ring.
- [x] Verify: Tab through the chapter list → delete buttons visible and focusable; delete works via Enter.

### Task 13: UploadZone keyboard path

**Dependencies:** None

Fixes `2.6`.

- [x] Remove `role="button" tabindex="0"` from the drop-area div (dead tab stop); the absolutely-positioned hidden file input already provides native keyboard activation (Tab + Enter/Space opens the picker).
- [x] Verify: Tab to the drop zone → Enter opens the file dialog; click still works; drag-drop unchanged.

### Task 14: KB search debounce

**Dependencies:** None

Fixes `2.7`.

- [x] `KnowledgePanel.svelte`: 250ms debounce on `oninput` search; clear the timer on unmount (`$effect` cleanup).
- [x] Verify: type a 6-char query → at most 1–2 `searchKnowledge` calls total (network tab).

### Task 15: Streaming markdown render debounce

**Dependencies:** None

Fixes `2.8` (marked re-parse of the full response on every delta).

- [x] `ChatPanel.svelte`: keep raw `currentResponse` for state, but render through a `displayResponse = $derived`-style 120ms debounced copy (`setTimeout` in an `$effect`, cleared on stream end).
- [x] Verify: long stream stays smooth; final text is complete (debounce flush on `onDone`).

### Task 16: Markdown sanitization + external link targets

**Dependencies:** None (new dep)

Fixes `2.12`.

- [x] `npm install dompurify` in `frontend`.
- [x] Create `frontend/src/lib/markdown.ts`: `renderMarkdown(text)` = `DOMPurify.sanitize(marked.parse(text, { async: false }))` + a marked renderer override so `<a>` gets `target="_blank" rel="noopener noreferrer"`.
- [x] Replace local `renderMarkdown` in `ChatPanel.svelte`, `LorePanel.svelte`, `KnowledgePanel.svelte` with the shared helper.
- [x] Verify: an entry containing `<img src=x onerror=alert(1)>` renders inert; chat links open in a new tab.

### Task 17: Panel state persistence + toolbar active states

**Dependencies:** None

Fixes `2.13`.

- [x] Book page: persist `showChat`, `showLore`, `chatWidth`, `loreWidth`, sidebar `collapsed` in `localStorage` under `book-ui-${slug}` (load on mount, save on change via `$effect`).
- [x] Toolbar Wiki/Chat buttons: add `class:active` (accent border/bg) when the corresponding panel is open.
- [x] Verify: open chat, resize to 600px, navigate away and back → state restored; open panel is visually distinct.

### Task 18: DiffOverlay accept/reject busy state

**Dependencies:** None

Fixes `2.9` (double-click double-submit).

- [x] `DiffOverlay`: `busy` prop/state; Accept/Reject disabled while `onAccept`/`onReject` promise is pending, label becomes "Accepting…"/"Rejecting…".
- [x] Verify: rapid double-click → single API call (network tab).

---

## Phase D — Feature polish

### Task 19: Wiki panel edit capability

**Dependencies:** None (backend PUT + frontend)

Fixes `3.5` (wiki is read-only; only the agent can edit).

- [x] Backend `LoreEndpoints.cs`: add `PUT /api/books/{slug}/lore/{category}/{entity}` — validate slug, category (`characters`/`locations`/`root`), entity ends with `.md`, no path traversal; write body content; return `{ saved: true, file }`.
- [x] `api.ts`: `saveWikiEntity(slug, category, entity, content)`.
- [x] `LorePanel.svelte`: ✏️ Edit button on the detail view → raw-markdown textarea (reuse the KnowledgePanel editor pattern) + Save/Cancel; Save calls `saveWikiEntity`, refreshes index, toast on failure; warn (confirm) when switching entities mid-edit.
- [x] Verify: edit a character page in the wiki panel → file changes on disk (`docker compose exec api cat /library/{slug}/wiki/characters/{x}.md`), list re-renders.

### Task 20: Regenerate Titles progress

**Dependencies:** None

Fixes `3.4` (menu closes, zero feedback, hardcoded 3s reload).

- [x] `ChapterSidebar.regenerateTitles()`: keep the menu open with the item in "Regenerating…" busy state; snapshot titles before the call; poll `loadChapters()` every 3s up to 120s; stop when any title differs from the snapshot → success toast; timeout → error toast.
- [x] Verify: trigger regenerate → busy state visible; titles update when the job finishes; failure paths surface a toast.

### Task 21: Chat message ergonomics

**Dependencies:** None

Fixes `3.7`.

- [x] Timestamps: relative time (`just now`, `5m ago`, or `HH:MM`) under each message role label (needs `createdAt` from history API — `ChatHistoryMessage` already carries it; for in-session messages use `Date.now()`).
- [x] Copy button (📋) on assistant messages (and long user messages) → `navigator.clipboard.writeText`, transient "Copied" state.
- [x] Autosize textarea: grow with content up to ~6 rows (`input` event sets `style.height`), reset after send.
- [x] Verify: history shows timestamps; copy works; multi-line input grows instead of scrolling inside 2 fixed rows.

### Task 22: Upload UX — size check + error layout

**Dependencies:** None

Fixes `3.2` (nowrap overflow of long error incl. full ACCEPTED list) and `3.3` (no client-side 100MB check).

- [x] `UploadZone` + library `+page.svelte`: before upload, if `file.size > 100 * 1024 * 1024` → friendly error "File is over the 100 MB limit."
- [x] Library page `.upload-error`: remove `white-space: nowrap`, cap `max-width: 90vw`, allow wrapping; same for `UploadZone` error box.
- [x] Verify: drop a >100MB file → clear message, no layout overflow; a 101MB real upload is rejected before hitting the server.

### Task 23: Chapter/wiki search + ellipsis tooltips

**Dependencies:** None

Fixes `3.6`.

- [x] `ChapterSidebar`: filter input above the chapter list (client-side, case-insensitive, matches title); clears with Escape.
- [x] `LorePanel`: same filter over entity names.
- [x] Add `title={...}` tooltips to truncated items: chapter items, wiki entities, `BookList` book titles.
- [x] Verify: type "chase" → only matching chapters shown; hover truncated title → full name.

### Task 24: Accessibility & keyboard pass

**Dependencies:** None

Fixes `3.9` remainder.

- [x] `ChatPanel`: `aria-live="polite"` on the messages container.
- [x] Book page: `Escape` closes the focused side panel (chat > lore > none).
- [x] Resize handles: keyboard-operable — `role="separator" aria-orientation="vertical"` + `onkeydown` ArrowLeft/Right adjusting width (reuse the same clamp logic).
- [x] `BookEditor`: `Ctrl+S` (and `Cmd+S`) in edit mode → flush pending save + "Saved" indicator.
- [x] Focus management: after Accept/Reject on `DiffOverlay`, return focus to the editor; after `InlineEditMenu` completes, focus stays in editor (already the case — no autofocus).
- [x] `InlineEditMenu`: clamp initial position to viewport (same math as the drag handler).
- [x] Verify: screen-reader pass over a chat stream announces new messages; Escape/arrows/`Ctrl+S` work as specced.

### Task 25: Small fixes bundle

**Dependencies:** None

Fixes `3.1`, `3.10`, `3.11`.

- [x] **Empty-book naming** (`3.1`): clicking "+ Empty Book" shows a small inline prompt (title input + Create/Cancel) instead of immediately creating "Untitled".
- [x] **Theme contrast** (`3.10`): build the frontend and visually inspect the editor pane. If `theme-nord` renders a light background (its default palette) inside the dark UI, set `.milkdown-wrapper { background: var(--bg-primary); }` and verify text contrast; if colors are still off, switch to an explicit dark palette override in `BookEditor` styles.
- [x] **KB chat pane resizable** (`3.11`): reuse the book-page drag-resize pattern for `.kb-chat-pane` (default 400, clamp 280–800).
- [x] **Dead code** (`3.11`): remove `.filter(cat => cat.entries.length > 0 || true)` in `KnowledgePanel.filteredCategories`.
- [x] **Generate Wiki consolidation** (`3.11`): unify labels between the ChapterSidebar menu item and the LorePanel button ("Generate Wiki"); both trigger `triggerLoreGeneration`.
- [x] **BookList error tooltip** (`3.11`): add `ErrorMessage` to `BookSummaryDto` (LibraryEndpoints) and render `title={book.errorMessage}` + an error line under failed books.
- [x] **Sort persistence** (`3.11`): persist `sortMode` in `localStorage`.
- [x] **Fragile selector** (`3.11`): replace `document.querySelector('.editor-pane .milkdown-wrapper')` in the book page with a `bind:this`-style callback from `BookEditor` (`onWrapperEl` prop).
- [x] **Hangfire link dev-only** (`3.11`): backend `/upload/status` returns `showHangfireLink` (true only when not `Testing` and env is Development); hide the link in production UI.
- [x] Verify: each item's behavior in the running stack; `dotnet test` still green after the DTO change.

---

## Coverage Matrix (sweep findings → tasks)

| Finding | Task | | Finding | Task |
|---|---|---|---|---|
| 1.1 save loss | 2 | | 2.9 diff busy | 18 |
| 1.2 silent failures | 1, 3 | | 2.10 wiki stale | 8 |
| 1.3 retry dedup | 4 | | 2.11 KB retry dead | 4 |
| 1.4 KB stop | 5 | | 2.12 sanitize | 16 |
| 1.5 session isolation | 6 | | 2.13 panel persistence | 17 |
| 1.6 stale menu | 7 | | 2.14 unsaved awareness | 2 |
| 1.7 lore error | 8 | | 3.1 empty-book name | 25 |
| 1.8 library error | 3 | | 3.2 error overflow | 22 |
| 2.1 stick-to-bottom | 9 | | 3.3 size check | 22 |
| 2.2 readonly select | 10 | | 3.4 titles progress | 20 |
| 2.3 load retry | 3 | | 3.5 wiki edit | 19 |
| 2.4 global stats | 11 | | 3.6 searches/tooltips | 23 |
| 2.5 delete affordances | 12 | | 3.7 chat ergonomics | 5, 21 |
| 2.6 upload keyboard | 13 | | 3.8 stop context loss | 5 (documented) |
| 2.7 search debounce | 14 | | 3.9 a11y | 12, 24 |
| 2.8 render debounce | 15 | | 3.10 theme contrast | 25 |
| — | — | | 3.11 misc | 25 |

**Note on 3.8** (chat Stop discards the whole agent session): intentionally not changed — aborting mid-generation leaves the agent session in an unknown state, and a fresh session is the safe default. Add a "Stop" tooltip: "Stops the response and resets the conversation context."
