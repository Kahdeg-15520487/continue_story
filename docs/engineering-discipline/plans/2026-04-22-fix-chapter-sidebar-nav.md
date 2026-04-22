# Fix Chapter Sidebar Navigation — Implementation Plan

> **Worker note:** Execute this plan task-by-task using the agentic-run-plan skill or subagents. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Fix chapter switching so clicking a sidebar chapter reliably loads and displays that chapter's content in the editor.

**Architecture:** The sidebar currently sets `activeChapterId` (via `$bindable`) *before* calling `onChapterSelect`. This causes the `{#key activeChapterId}` block to remount `BookEditor` with the **old** content before the API returns the new content. A second race condition: the debounced save can fire between chapter switch initiation and API return, overwriting the target chapter with the current chapter's content. Fix both by (1) having the sidebar only call `onChapterSelect` (never set `activeChapterId` directly), and (2) canceling the debounced save before switching. The parent sets `activeChapterId` *after* `content` is updated, so `{#key}` remounts with correct content.

**Tech Stack:** Svelte 5 (`$props`, `$state`, `$effect`, `$bindable`, `{#key}`), Milkdown v7 editor, ASP.NET minimal API

**Work Scope:**
- **In scope:** Fix chapter switching race condition in sidebar/page coordination; fix debounced save race during switch; exclude `.scratch.md` files from all `ChapterService` file listing methods
- **Out of scope:** Delete confirmation (already implemented); frontend tests; other sidebar UX improvements

**Verification Strategy:**
- **Level:** build-only (no frontend test framework exists)
- **Command:** `docker compose up -d --build 2>&1 | tail -5` then `curl -s http://localhost:5000/api/books/fresh-test/chapters` to verify scratch exclusion; manual browser test for chapter switching
- **What it validates:** Backend builds and runs, scratch files excluded from chapter listing, frontend compiles without errors

---

### File Structure Mapping

| File | Action | Responsibility |
|---|---|---|
| `frontend/src/lib/components/ChapterSidebar.svelte` | Modify | Remove direct `activeChapterId` assignments from click handlers; only delegate to `onChapterSelect` callback |
| `frontend/src/routes/books/[slug]/+page.svelte` | Modify | Cancel debounced save in `handleChapterSelect` before async work |
| `backend/src/KnowledgeEngine.Api/Services/ChapterService.cs` | Modify | Exclude `.scratch.md` from remaining `GetFiles` calls (lines 176, 198, 215) |

---

### Task 1: Fix sidebar — stop setting `activeChapterId` directly

**Dependencies:** None (can run in parallel with Task 3)
**Files:**
- Modify: `frontend/src/lib/components/ChapterSidebar.svelte`

- [ ] **Step 1: Remove `activeChapterId = chapter.id` from chapter click handler**

In the `{#each}` block, change the `onclick` handler on `.chapter-item` from:
```svelte
onclick={() => { activeChapterId = chapter.id; onChapterSelect?.(chapter.id); }}
```
to:
```svelte
onclick={() => onChapterSelect?.(chapter.id)}
```

- [ ] **Step 2: Remove `activeChapterId = created.id` from `addChapter()`**

In the `addChapter()` function, change:
```ts
activeChapterId = created.id;
onChapterSelect?.(created.id);
```
to:
```ts
onChapterSelect?.(created.id);
```

- [ ] **Step 3: Simplify `removeChapter()` — delegate active selection to parent**

Change the `if (activeChapterId === id)` block in `removeChapter()` from:
```ts
if (activeChapterId === id) {
  activeChapterId = chapters.length > 0 ? chapters[0].id : null;
  if (activeChapterId) onChapterSelect?.(activeChapterId);
}
```
to:
```ts
if (activeChapterId === id) {
  const next = chapters.length > 0 ? chapters[0].id : null;
  if (next) onChapterSelect?.(next);
  else activeChapterId = null;
}
```
The `else activeChapterId = null` branch is safe — it sets `activeChapterId` to `null` only when there are zero chapters remaining (no `onChapterSelect` call needed, and `null` won't trigger the `{#key}` block in a harmful way since `content` was from the deleted chapter).

- [ ] **Step 4: Verify frontend compiles**

Run: `cd frontend && npx vite build 2>&1 | tail -5`
Expected: Build succeeds with no errors.

---

### Task 2: Fix page — cancel debounced save during chapter switch

**Dependencies:** Task 1 (same file component contract — `onChapterSelect` is now the sole mechanism for chapter switching)
**Files:**
- Modify: `frontend/src/routes/books/[slug]/+page.svelte`

- [ ] **Step 1: Cancel pending save at the top of `handleChapterSelect`**

At the beginning of `handleChapterSelect()`, before the diff cleanup block, add:
```ts
if (saveTimeout) { clearTimeout(saveTimeout); saveTimeout = null; }
```

The full function should become:
```ts
async function handleChapterSelect(id: string) {
    // Cancel any pending save so it doesn't overwrite the target chapter
    if (saveTimeout) { clearTimeout(saveTimeout); saveTimeout = null; }
    // Clean up any active diff when switching chapters
    if (diffState && activeChapterId) {
      try { await api.rejectInlineEdit(slug, activeChapterId); } catch { /* ignore */ }
      diffState = null;
    }
    showInlineEdit = false;
    if (!slug) return;
    try {
      const chapter = await api.getChapter(slug, id);
      if (chapter) {
        content = chapter.content;
        activeChapterId = id;
      }
    } catch { /* ignore */ }
  }
```

Key ordering: `content` is set **before** `activeChapterId` so `{#key activeChapterId}` remounts `BookEditor` with the correct content already in place.

- [ ] **Step 2: Verify frontend compiles**

Run: `cd frontend && npx vite build 2>&1 | tail -5`
Expected: Build succeeds with no errors.

---

### Task 3: Exclude `.scratch.md` from all `ChapterService` file operations

**Dependencies:** None (can run in parallel with Tasks 1 & 2)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/ChapterService.cs`

Three `GetFiles` calls still lack the `.scratch.md` filter. All need the same `.Where(f => !f.EndsWith(".scratch.md"))` clause.

- [ ] **Step 1: Add filter to `DeleteChapterAsync` renumber logic (~line 176)**

Change:
```csharp
var remaining = Directory.GetFiles(dir, "ch-*.md").OrderBy(f => f).ToList();
```
to:
```csharp
var remaining = Directory.GetFiles(dir, "ch-*.md")
    .Where(f => !f.EndsWith(".scratch.md"))
    .OrderBy(f => f).ToList();
```

- [ ] **Step 2: Add filter to `ReorderChaptersAsync` — read loop (~line 198)**

Change:
```csharp
foreach (var f in Directory.GetFiles(dir, "ch-*.md"))
```
to:
```csharp
foreach (var f in Directory.GetFiles(dir, "ch-*.md").Where(f2 => !f2.EndsWith(".scratch.md")))
```

Note: lambda param is `f2` to avoid conflict with outer `f`. Actually the foreach variable IS `f`, so:
```csharp
foreach (var file in Directory.GetFiles(dir, "ch-*.md").Where(x => !x.EndsWith(".scratch.md")))
{
    var id = Path.GetFileNameWithoutExtension(file);
    var content = File.ReadAllText(file);
    var title = file.Substring(file.LastIndexOf("ch-") + 7);
    title = title.Substring(0, title.Length - 3);
    chapters[id] = (title, content);
}
```

- [ ] **Step 3: Add filter to `ReorderChaptersAsync` — delete loop (~line 215)**

Change:
```csharp
foreach (var f in Directory.GetFiles(dir, "ch-*.md"))
    File.Delete(f);
```
to:
```csharp
foreach (var f in Directory.GetFiles(dir, "ch-*.md").Where(x => !x.EndsWith(".scratch.md")))
    File.Delete(f);
```

- [ ] **Step 4: Verify backend builds and tests pass**

Run: `cd backend && dotnet build KnowledgeEngine.sln 2>&1 | grep -E "error|Build succeeded"`
Run: `cd backend && dotnet test 2>&1 | tail -3`
Expected: Build succeeds, all tests pass.

---

### Task 4 (Final): End-to-End Verification

**Dependencies:** Tasks 1, 2, 3 all complete
**Files:** None (read-only verification)

- [ ] **Step 1: Build and deploy**

Run: `cd /project && docker compose up -d --build 2>&1 | tail -5`
Expected: All containers start successfully.

- [ ] **Step 2: Verify scratch files excluded from chapter listing**

Run: `curl -s http://localhost:5000/api/books/fresh-test/chapters`
Expected: No `.scratch` entries in the response. Only real chapter files listed.

- [ ] **Step 3: Verify chapter switching works via API**

Run these two commands and confirm they return different content:
```bash
CHAPTER1_ID=$(curl -s http://localhost:5000/api/books/fresh-test/chapters | python3 -c "import sys,json; print(json.load(sys.stdin)[0]['id'])")
CHAPTER2_ID=$(curl -s http://localhost:5000/api/books/fresh-test/chapters | python3 -c "import sys,json; print(json.load(sys.stdin)[1]['id'])")
echo "Chapter 1 ID: $CHAPTER1_ID"
echo "Chapter 2 ID: $CHAPTER2_ID"
curl -s "http://localhost:5000/api/books/fresh-test/chapters/$CHAPTER1_ID" | python3 -c "import sys,json; d=json.load(sys.stdin); print(f'Ch1 title: {d[\"title\"]}')"
curl -s "http://localhost:5000/api/books/fresh-test/chapters/$CHAPTER2_ID" | python3 -c "import sys,json; d=json.load(sys.stdin); print(f'Ch2 title: {d[\"title\"]}')"
```
Expected: Each chapter returns its own distinct title and content.

- [ ] **Step 4: Manual browser verification**

Open `http://localhost:5173/books/fresh-test` in a browser and verify:
- [ ] Clicking a chapter in the sidebar loads that chapter's content in the editor
- [ ] The active chapter is highlighted in the sidebar
- [ ] Clicking rapidly between chapters does not show wrong content
- [ ] The delete button shows a confirmation dialog before deleting

---

## Self-Review

**1. Spec coverage:**
- ✅ Chapter switching race condition → Task 1 (remove sidebar's `activeChapterId` set) + Task 2 (ordering fix already in place)
- ✅ Debounced save race condition → Task 2 (cancel save before switch)
- ✅ Scratch file exclusion → Task 3 (covers all 3 remaining `GetFiles` calls)
- ✅ Delete confirmation → Already implemented (not in scope)

**2. Placeholder scan:**
- ✅ All code blocks contain exact code to write
- ✅ All commands have expected output
- ✅ No "TBD", "TODO", or "implement later" strings

**3. Type consistency:**
- ✅ `onChapterSelect` callback signature `(id: string) => void` consistent across all call sites
- ✅ `activeChapterId: string | null` type preserved
- ✅ `saveTimeout` referenced consistently

**4. Dependency verification:**
- Task 1 and Task 3 are parallelizable (different files)
- Task 2 depends on Task 1 (ensures `onChapterSelect` is the sole mechanism)
- Task 4 depends on all tasks (final verification)
- ✅ No file conflicts between parallel tasks

**5. Verification coverage:**
- ✅ Task 4 includes build verification, API verification, and manual browser checklist
- ✅ Backend tests run in Task 3
- ✅ Frontend build check in Tasks 1 and 2
