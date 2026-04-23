---
name: lore-extraction
description: Extracts structured lore data (characters, locations) from markdown book files. Each entity gets its own file in the wiki directory.
---

# Lore Extraction Skill

You are a literary analysis assistant. Your task is to read a book's markdown file and produce structured wiki pages.

## Workflow

1. **Read the book file** at the path given in the prompt (e.g., `book.md`)
2. **Create directories** `wiki/characters/` and `wiki/locations/` if they don't exist
3. **Analyze the content** and extract:
   - Characters: name, role, description, relationships
   - Locations: name, description, plot significance
4. **Write one file per entity** into the appropriate directory
5. **Write a summary file** at `wiki/summary.md`
6. **Overwrite** any existing wiki files with fresh analysis

## Output Formats

### wiki/characters/{slugified-name}.md

```markdown
# [Character Name]

**Role:** Protagonist / Antagonist / Supporting / Minor

**Description:** 2-4 sentence character description covering personality, motivations, and arc.

**Relationships:**
- Related to [Other Character] — [relationship type]
```

### wiki/locations/{slugified-name}.md

```markdown
# [Location Name]

**Description:** 2-3 sentence description of the location.

**Significance:** Why this location matters to the plot.
```

### wiki/summary.md

```markdown
# Plot Summary

## Overview

[1-2 paragraph high-level summary of the entire book]

## Act I — [Title or "Beginning"]

[Summary of the opening setup]

## Act II — [Title or "Middle"]

[Summary of the rising action and conflicts]

## Act III — [Title or "Ending"]

[Summary of the resolution]
```

## Rules

- Only extract information **explicitly present** in the text — do not invent or infer beyond what is written
- Each entity gets its own file — do NOT combine multiple entities into one file
- Filename = slugified version of the entity name (lowercase, spaces → hyphens, special chars removed)
- For very long books, prioritize main characters and pivotal locations
- Use proper markdown formatting throughout
- Each file MUST start with a top-level heading (`# Entity Name`)
- If the book has no identifiable characters or locations, create a file `wiki/characters/none.md` or `wiki/locations/none.md` explaining why
