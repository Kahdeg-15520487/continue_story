# UI/UX Sweep — Findings (2026-08-02)

Comprehensive review of all frontend components plus UX-affecting backend/agent behavior.
Every finding includes file reference and a suggested fix. Grouped by priority.

---

## P1 — Data loss & correctness bugs (fix first)

### 1.1 Chapter switch / Lock silently drops the last ~1s of edits
`frontend/src/routes/books/[slug]/+page.svelte`
- `debouncedSave()` waits 1s before calling `saveContent()`.
- `handleChapterSelect()` **cancels the pending save timeout** — edits made in the last second before switching chapters are silently discarded.
- Clicking **Lock** doesn't cancel the timeout, but `saveContent()` early-returns `if (!isEditing)` — same silent loss.
**Fix:** flush the pending save synchronously before switching/locking (await `saveContent` with a fresh snapshot), and/or show an unsaved-changes indicator + `beforeunload` guard when `saveTimeout` is pending.

### 1.2 Save failures are silent everywhere (no toast/notification system)
Console-only `catch` blocks in: `saveContent`, `saveTitle`, `saveEdit` (KnowledgePanel), `addChapter`/`removeChapter`/`regenerateTitles`/`generateWiki` (ChapterSidebar), `deleteBook` (BookList), `loadBooks` (+page), `handleRejectEdit`, `loadIndex` (KnowledgePanel).
User believes content is saved; on network failure it isn't.
**Fix:** add a lightweight toast/notification store; surface every failed mutation. This is the single highest-leverage systemic change.

### 1.3 Chat Retry duplicates the user message bubble
`ChatPanel.svelte` `retry()`: only removes the last message if it's an *assistant* message. After a failed send, the last message is the user's own — retry appends a duplicate user bubble.
**Fix:** track the original user message index; replace instead of append.

### 1.4 KB chat "Stop" doesn't actually stop anything
`ChatPanel.svelte` knowledge mode `stop()` only aborts the frontend fetch. The backend `/api/knowledge/chat` handler keeps streaming into a dead connection and the agent session **keeps running** (may keep writing KB files). The partial response is never saved.
**Fix:** call `POST /api/knowledge/chat/abort` (endpoint exists, unused) before aborting the fetch.

### 1.5 "New Session" doesn't isolate history
`startNewSession()` clears the in-memory list, but history is loaded **without a sessionId filter** (`api.getChatHistory(slug, 100)`), so after any reload all past sessions' messages come back mixed together. "+ New Session" feels broken.
**Fix:** load history filtered by `currentSessionId`; the backend already supports `?sessionId=`.

### 1.6 Chat edit pops up the stale inline-edit menu
`handleChatEditDone()` sets `showInlineEdit = true` to reveal the diff — but that flag also renders `InlineEditMenu` at the **stale selection coordinates** with stale `selectedText` (only when `isEditing`). A floating "Describe the edit..." box appears at a random old position over the diff.
**Fix:** use a separate flag for "diff visible" (or set `showInlineEdit = false` and rely on `diffState` alone).

### 1.7 LorePanel swallows entity load errors
`LorePanel.svelte` `selectEntity()` puts the error into `content`, but the view renders `renderedHtml` only — user sees the misleading "Select an entity to view." placeholder.
**Fix:** set `wikiError` instead.

### 1.8 Library load failure shows a misleading empty state
`+page.svelte` `loadBooks()` catch is console-only; sidebar then renders `BookList` with `books = []` → "No books yet. Create one to get started." + drop zone, as if the library were empty.
**Fix:** show an error banner with a Retry button when the initial load fails.

---

## P2 — Significant UX issues

### 2.1 Chat auto-scroll yanks the user to the bottom while reading history
`ChatPanel.svelte` `$effect` unconditionally sets `scrollTop = scrollHeight` on every delta. Scrolling up to re-read during a long response is impossible.
**Fix:** "stick to bottom" pattern — only auto-scroll when the user is already within ~40px of the bottom.

### 2.2 Readonly editor blocks text selection/copy
`BookEditor.svelte` sets `pointer-events: none` on the editor in readonly mode. Users cannot select or copy passages (and the agent's "read-only" browsing mode is the default state).
**Fix:** use `contenteditable="false"` + `user-select: text`; only block editing, not selection.

### 2.3 No error/retry path on book page load failure
`books/[slug]/+page.svelte` error screen shows the message + back link only. A transient API failure forces full navigation away and back.
**Fix:** add a Retry button that calls `loadBook()`.

### 2.4 Conversion panel shows GLOBAL Hangfire job counts
`UploadEndpoints.cs` `/status` returns `monitoring.EnqueuedCount("default")`, `ProcessingCount()`, etc. — these are all-book totals. With two books converting, numbers are misleading.
**Fix:** filter by the book's job (`jobId`), or rename to "system" stats.

### 2.5 Delete actions invisible on touch / keyboard
- `ChapterSidebar` delete is `display:none` until hover → unreachable by keyboard (removed from tab order) and touch.
- `BookList` delete is `opacity:0` until hover → focusable but invisible, no `:focus-visible` style.
**Fix:** always-visible low-emphasis buttons (or reveal on `:hover, :focus-within`), add focus styles.

### 2.6 UploadZone has a dead tab stop
`drop-area` has `role="button" tabindex="0"` but no key handler; the invisible file input beneath is also focusable → two tab stops, one does nothing. Keyboard users can't trigger the file dialog from the "button".
**Fix:** remove `role/tabindex` from the div (the input handles keyboard activation) or wire Enter/Space to `input.click()`.

### 2.7 KB search fires on every keystroke, no debounce
`KnowledgePanel.svelte` `oninput` → `doSearch()` per keystroke; each call scans every file in the KB server-side.
**Fix:** 250–300ms debounce.

### 2.8 Markdown re-parsed on every stream delta
`ChatPanel` calls `renderMarkdown(currentResponse)` (marked.parse over the entire response) on every `text_delta` — O(n²) work and janky re-renders on long responses.
**Fix:** debounce rendering (e.g. 100–150ms) and/or memoize by content length; consider sanitizing (see 2.12).

### 2.9 DiffOverlay is heavy for long chapters
Every line of the full chapter becomes a DOM div (`diffLines` + `split('\n')`). 5–10k-word chapters → thousands of nodes; no busy state on Accept/Reject (double-click = double API call).
**Fix:** cap/limit rendered diff, add disabled state while accepting, virtualize if needed.

### 2.10 Wiki panel goes stale after agent chat edits
`LorePanel` loads the index once on mount; the agent frequently rewrites `wiki/*` via chat — panel shows outdated entities until reopened.
**Fix:** refresh index on `edit_done`/`onResponseDone` (the book page already receives these events).

### 2.11 Retry button visible but inert in KB chat
`ChatPanel` shows ↻ Retry whenever `messages.length > 0`, but `retry()` returns early for `mode === 'knowledge'`.
**Fix:** hide the button in KB mode (or implement it).

### 2.12 Unsanitized LLM markdown + links open in the same tab
`marked.parse` output is injected via `{@html}` in ChatPanel, LorePanel, KnowledgePanel. No sanitizer; agent output could include `<script>`/`<img onerror>`; links have no `target="_blank"`.
**Fix:** DOMPurify before `@html`; render links with `target="_blank" rel="noopener"` (post-process or a marked renderer override).

### 2.13 Panel state doesn't persist; no active toggle state
Toolbar Wiki/Chat toggle buttons don't indicate which panel is open (no `class:active`); panel open/closed state and widths (default 400px) reset on every navigation; sidebar collapse resets. Reading position *is* persisted — inconsistent.
**Fix:** persist in `localStorage` per book; add active styles.

### 2.14 No unsaved-changes awareness anywhere
No dirty flag, no `beforeunload` warning, no "Saving…" state beyond a brief label. Combined with 1.1/1.2, users can lose work without knowing.
**Fix:** dirty tracking + `beforeunload` + visible "Saved/Unsaved changes" indicator in the toolbar.

---

## P3 — Polish & nice-to-haves

### 3.1 Empty book creation named "Untitled"
`createEmptyBook()` hardcodes title "Untitled"; user renames after landing. A small inline name prompt (or a dialog) would feel more deliberate. Also "Untitled-2" collisions are handled by slug dedup, but the display title stays "Untitled".

### 3.2 Upload error pill overflows
Library page `.upload-error` uses `white-space: nowrap` while the unsupported-type message embeds the entire `ACCEPTED` list. Long errors overflow the viewport.
**Fix:** remove `nowrap`, allow wrap, cap width.

### 3.3 No client-side upload size check
Backend limit is 100MB; oversize files fail with a generic `HTTP 413` error. Check `file.size` before uploading and show a friendly message.

### 3.4 "Regenerate Titles" gives no visible progress
`ChapterSidebar` closes the menu on click and just reloads after a hardcoded 3s `setTimeout`. If the job takes longer, nothing indicates work is happening.
**Fix:** use a banner/poll (like conversion) or at least keep the menu item in a busy state.

### 3.5 Wiki is read-only for users
`LorePanel` offers no editing; only the agent can update wiki files (via chat). Knowledge base has full edit UI; wiki doesn't.
**Fix:** add edit (raw markdown) + refresh to LorePanel, reusing the KB editor pattern.

### 3.6 No search in chapter list / wiki list
Long novels (100+ chapters) and large wikis require scrolling. Add a filter input; add `title` tooltips to ellipsized titles (chapters, entities, books).

### 3.7 Chat message ergonomics
No timestamps, no copy button, no per-message actions; textarea is fixed 2 rows (no autosize); partial response is discarded when the stream errors mid-way.
**Fix:** timestamps, copy action, autosize textarea, keep partial text on error.

### 3.8 Chat "Stop" discards the whole agent session (context loss)
Book-mode abort kills the agent session server-side (`disposeSession("client request")`); the next message starts with zero context. Users may expect stop ≠ reset. Consider session compaction instead, or label the action clearly ("Stop & reset context").

### 3.9 Keyboard & a11y gaps
- Resize handles (`role="separator"`) not keyboard-operable (no arrow-key support).
- No `aria-live` region for streaming chat — screen readers hear nothing.
- `.title-input` and KB search input set `outline: none` — no focus indicator.
- No focus management after accepting/rejecting a diff; no Escape-to-close for side panels.
- Add keyboard shortcuts: `Ctrl+S` save, `Ctrl+Enter` send (Enter already works), `[`/`]` chapter nav.

### 3.10 Verify editor theme contrast
`BookEditor` sets `.milkdown { color: var(--text-primary) }` (light gray) but never sets a background. `@milkdown/theme-nord` ships a light palette by default — verify the editor pane doesn't render light-on-light (or an odd light box inside the dark UI). Couldn't verify statically (no node_modules locally). If broken: override background or switch to a dark theme.

### 3.11 Miscellaneous small things
- Knowledge page chat pane is fixed 400px and not resizable (book page panels are) — inconsistent.
- `filteredCategories` contains dead code: `.filter(cat => cat.entries.length > 0 || true)`.
- "Generate Wiki" exists in three places (sidebar menu, LorePanel empty state, error retry) with different behaviors — consolidate.
- `BookList` error books show only ❌ with no tooltip/message until clicked.
- Sort preference (Last Modified / A–Z) not persisted.
- `saveReadingPosition()` uses a global `document.querySelector('.editor-pane .milkdown-wrapper')` — fragile; pass the element via callback/binding instead.
- Conversion "View in Hangfire Dashboard" link exposes dev tooling in the user UI — hide in non-dev environments.

---

## What already works well (keep)
- Tool-call activity blocks with human-readable labels (`tool-labels.ts`) — great transparency feature.
- Diff review flow (Original / Edited / Diff tabs, line stats) before accepting AI edits.
- Reading-position persistence per book.
- Drag-to-resize side panels; `Escape` handled in inline-edit menu; Enter-to-send with Shift+Enter newline.
- Debounced saves, scratch-file recovery on reload, interrupted-task banner, idle-session cleanup.
- KB search with tag autocomplete and namespaced tags.

---

## Suggested implementation order
1. **Systemic:** toast/error store (1.2) — everything else reports through it.
2. **Data-loss fixes:** flush-on-switch/lock (1.1), unsaved indicator (2.14), retry dedup (1.3).
3. **Correctness:** KB stop (1.4), session history isolation (1.5), stale inline-edit menu (1.6), LorePanel error (1.7), library error state (1.8).
4. **Interaction:** stick-to-bottom scroll (2.1), readonly selection (2.2), load-retry (2.3), delete affordances (2.5/2.6), search debounce (2.7), stream render debounce (2.8), panel persistence (2.13).
5. **Safety:** markdown sanitization + link targets (2.12).
6. **Polish:** P3 backlog.
