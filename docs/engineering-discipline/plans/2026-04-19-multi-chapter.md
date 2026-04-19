# Multi-Chapter Story Support

## Overview
After book conversion, automatically split `book.md` into individual chapter files (`chapters/ch-001-title.md`). The UI shows a chapter sidebar for navigation. Lore generation produces both whole-book wiki (characters, locations, themes, summary) and per-chapter summaries.

## Architecture

### File Structure (per book)
```
/library/{slug}/
  book.md              # original converted markdown (kept as reference)
  chapters/
    ch-001-the-awakening.md
    ch-002-the-journey.md
    ...
  wiki/
    characters.md
    locations.md
    themes.md
    summary.md         # whole-book summary
    chapter-summaries.md  # per-chapter summaries
```

### Data Flow
```
Upload → Convert → Split into Chapters → Generate Lore (whole-book + chapter summaries)
```

---

## Task 1: Chapter Splitting (Backend)

### ChapterEndpoints.cs — new file
- `GET /api/books/{slug}/chapters` — list chapters (ordered)
- `GET /api/books/{slug}/chapters/{id}` — get chapter content
- `PUT /api/books/{slug}/chapters/{id}` — save chapter content
- `POST /api/books/{slug}/chapters` — insert new chapter
- `DELETE /api/books/{slug}/chapters/{id}` — delete chapter
- `POST /api/books/{slug}/chapters/reorder` — reorder chapters

### ChapterService.cs
- `ListChaptersAsync(slug)` — read `chapters/` dir, sort by filename, return metadata
- `GetChapterAsync(slug, id)` — read individual chapter file
- `SaveChapterAsync(slug, id, content)` — write chapter file
- `InsertChapterAsync(slug, title, afterChapterId?)` — create new chapter file with correct numbering
- `DeleteChapterAsync(slug, id)` — remove file, renumber siblings
- `ReorderChaptersAsync(slug, orderedIds)` — rename files to match new order

### Chapter split algorithm
After conversion completes (in `ConversionJobService.UpdateBookAfterConversion`):
1. Agent reads `book.md` and splits into chapters
2. Creates `chapters/` directory with individual files
3. Filename format: `ch-NNN-{slugified-title}.md`
4. Each file starts with `# {Chapter Title}` heading
5. If no chapter headings detected, entire book becomes `ch-001-untitled.md`
6. `book.md` is kept unchanged as the source of truth for re-splits

### New book statuses
- `splitting` — agent is splitting book into chapters (between converting and generating-lore)

---

## Task 2: Agent Chapter Splitting

### Agent prompt (in ConversionJobService)
After conversion, create a write-mode session and prompt:
```
Read book.md and split it into chapter files in the chapters/ directory.
- Detect chapter boundaries by headings (## Chapter 1, # Part One, etc.)
- Each chapter gets its own file: ch-NNN-{slugified-title}.md
- Each file starts with the chapter heading as an H1
- Include all content up to the next chapter heading
- If no clear chapter structure, create ch-001-untitled.md with the full content
```

This uses the existing write session infrastructure.

---

## Task 3: Chapter Navigation (Frontend)

### ChapterSidebar component
- Left sidebar showing chapter list (collapsible)
- Each chapter shows: number, title, word count
- Click to navigate to that chapter in the editor
- Drag to reorder (stretch goal — defer)
- "+" button to add new chapter
- Context menu: rename, delete

### Editor changes
- Instead of loading `book.md` into the editor, load the selected chapter
- Tab bar at top showing open chapters
- Chapter content saves to individual files via `PUT /api/books/{slug}/chapters/{id}`

### +page.svelte changes
- Add `ChapterSidebar` to the left of the editor
- Track `activeChapterId` state
- Load chapter content instead of full book content
- Status flow: converting → splitting → generating-lore → lore-ready

---

## Task 4: Per-Chapter Lore Summaries

### LoreJobService changes
After generating whole-book wiki files, also generate `wiki/chapter-summaries.md`:
- Agent reads each chapter and writes a 2-3 sentence summary
- Formatted as markdown with `## Chapter N: Title` headings
- Stored alongside existing wiki files

### Wiki prompt addition
```
After generating the wiki files, read each chapter in chapters/ and create 
wiki/chapter-summaries.md with a brief summary of each chapter.
```

---

## Task 5: Chat Context for Chapters

### ChatEndpoints changes
- When user asks about a chapter, inject that chapter's content as context
- Agent has access to all chapter files via tools
- Chat panel shows "Discussing: Chapter 3" indicator when focused

### Agent session context
- Fresh session injection includes chapter list + active chapter content
- Wiki context includes chapter summaries

---

## Execution Order
1. **Task 1** — Backend chapter endpoints + service (no agent yet, manual split)
2. **Task 2** — Agent splitting in conversion pipeline
3. **Task 3** — Frontend chapter navigation + editor
4. **Task 4** — Per-chapter lore summaries
5. **Task 5** — Chat chapter context

Tasks 1-2 are the foundation. Task 3 is the biggest UI change. Tasks 4-5 are additive.
