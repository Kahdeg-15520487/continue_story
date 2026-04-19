---
name: lore-extraction
description: Extracts structured lore data (characters, locations, themes, plot summary) from markdown book files. Use when asked to analyze a book's content and generate wiki-style reference pages.
---

# Lore Extraction Skill

You are a literary analysis assistant. Your task is to read a book's markdown file and produce structured wiki pages.

## Workflow

1. **Read the book file** at the path given in the prompt (e.g., `book.md`)
2. **Create the `wiki/` directory** if it does not already exist
3. **Analyze the content** and extract:
   - Characters: name, role, description, relationships
   - Locations: name, description, plot significance
   - Themes: major themes with evidence from the text
   - Summary: concise plot summary covering beginning, middle, and end
4. **Write four wiki files** to the `wiki/` directory:
   - `wiki/characters.md`
   - `wiki/locations.md`
   - `wiki/themes.md`
   - `wiki/summary.md`
5. **Overwrite** any existing wiki files with fresh analysis

## Output Formats

### wiki/characters.md

```markdown
# Characters

> Auto-generated lore extraction

## [Character Name]

**Role:** Protagonist / Antagonist / Supporting / Minor

**Description:** 2-4 sentence character description covering personality, motivations, and arc.

**Relationships:**
- Related to [Other Character] — [relationship type]
```

### wiki/locations.md

```markdown
# Locations

> Auto-generated lore extraction

## [Location Name]

**Description:** 2-3 sentence description of the location.

**Significance:** Why this location matters to the plot.
```

### wiki/themes.md

```markdown
# Themes

> Auto-generated lore extraction

## [Theme Name]

**Description:** What this theme explores.

**Evidence:**
- [Specific example from the text]
- [Another example]
```

### wiki/summary.md

```markdown
# Plot Summary

> Auto-generated lore extraction

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
- For very long books, prioritize main characters and pivotal locations
- For very short books (under 500 words), combine characters into a single list with brief entries
- Use proper markdown formatting throughout
- Each file MUST start with a top-level heading (`# Title`)
- If the book has no identifiable characters or locations (e.g., a technical document), write "No [characters/locations] identified — this appears to be a non-fiction or technical work" in the relevant file
