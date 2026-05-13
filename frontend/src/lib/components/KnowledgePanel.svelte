<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { marked } from 'marked';

  interface KBEntry { file: string; title: string; tags: string[]; }
  interface KBCategory { name: string; entries: KBEntry[]; }

  let categories = $state<KBCategory[]>([]);
  let allTags = $state<string[]>([]);
  let selectedTags = $state<string[]>([]);
  let searchQuery = $state('');
  let searchResults = $state<Array<{ category: string; file: string; title: string; tags: string[] }> | null>(null);
  let selectedCategory = $state<string | null>(null);
  let selectedEntry = $state<string | null>(null);
  let entryContent = $state('');
  let loading = $state(true);
  let editing = $state(false);
  let editContent = $state('');
  let showNewEntry = $state(false);
  let newEntryName = $state('');

  async function loadIndex() {
    try {
      const data = await api.getKnowledgeIndex();
      categories = data.categories;
      allTags = data.tags;
    } catch (err) {
      console.error('Failed to load KB index:', err);
    } finally {
      loading = false;
    }
  }

  async function doSearch() {
    const q = searchQuery.trim();
    const tags = selectedTags.length > 0 ? selectedTags : undefined;
    if (!q && !tags) {
      searchResults = null;
      return;
    }
    try {
      const data = await api.searchKnowledge(q || undefined, tags);
      searchResults = data.results;
    } catch (err) {
      console.error('Search failed:', err);
    }
  }

  function toggleTag(tag: string) {
    if (selectedTags.includes(tag)) {
      selectedTags = selectedTags.filter(t => t !== tag);
    } else {
      selectedTags = [...selectedTags, tag];
    }
    doSearch();
  }

  let tagSuggestions = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return [];
    return allTags
      .filter(t => t.toLowerCase().includes(q) && !selectedTags.includes(t))
      .slice(0, 8);
  });

  let filteredCategories = $derived.by(() => {
    if (searchResults !== null) return null;
    if (selectedTags.length === 0) return categories;
    return categories.map(cat => ({
      ...cat,
      entries: cat.entries.filter(e => selectedTags.some(t => e.tags.map(x => x.toLowerCase()).includes(t.toLowerCase()))),
    })).filter(cat => cat.entries.length > 0 || true); // always show categories
  });

  async function selectEntry(category: string, file: string) {
    selectedCategory = category;
    selectedEntry = file;
    editing = false;
    try {
      const data = await api.getKnowledgeEntry(category, file);
      entryContent = data.content;
    } catch (err) {
      entryContent = 'Failed to load entry.';
    }
  }

  function startEdit() {
    editContent = entryContent;
    editing = true;
  }

  async function saveEdit() {
    if (!selectedCategory || !selectedEntry) return;
    try {
      await api.saveKnowledgeEntry(selectedCategory, selectedEntry, editContent);
      entryContent = editContent;
      editing = false;
      await loadIndex();
    } catch (err) {
      console.error('Failed to save:', err);
    }
  }

  async function createEntry() {
    if (!selectedCategory || !newEntryName.trim()) return;
    const name = newEntryName.trim().endsWith('.md') ? newEntryName.trim() : newEntryName.trim() + '.md';
    const title = name.replace('.md', '');
    try {
      await api.saveKnowledgeEntry(selectedCategory, name, `---\ntitle: ${title}\ntags: []\n---\n\n# ${title}\n\n`);
      newEntryName = '';
      showNewEntry = false;
      await loadIndex();
      selectEntry(selectedCategory, name);
    } catch (err) {
      console.error('Failed to create entry:', err);
    }
  }

  async function deleteEntry(category: string, file: string) {
    if (!confirm(`Delete "${file}"?`)) return;
    try {
      await api.deleteKnowledgeEntry(category, file);
      if (selectedCategory === category && selectedEntry === file) {
        selectedEntry = null;
        entryContent = '';
      }
      await loadIndex();
    } catch (err) {
      console.error('Failed to delete:', err);
    }
  }

  async function deleteCategory(name: string) {
    if (!confirm(`Delete entire category "${name}" and all its entries?`)) return;
    try {
      await api.deleteKnowledgeCategory(name);
      if (selectedCategory === name) {
        selectedCategory = null;
        selectedEntry = null;
        entryContent = '';
      }
      await loadIndex();
    } catch (err) {
      console.error('Failed to delete category:', err);
    }
  }

  function clearSearch() {
    searchQuery = '';
    selectedTags = [];
    searchResults = null;
  }

  function stripFrontmatter(md: string): string {
    if (!md.startsWith('---')) return md;
    const end = md.indexOf('\n---', 3);
    if (end < 0) return md;
    return md.slice(end + 4).trimStart();
  }

  let renderedHtml = $derived(entryContent ? marked.parse(stripFrontmatter(entryContent), { async: false }) as string : '');

  onMount(loadIndex);
</script>

<div class="knowledge-panel">
  <div class="kb-sidebar">
    <div class="kb-sidebar-header">
      <h3>Knowledge Base</h3>
      <div class="kb-actions">
        <button onclick={() => showNewEntry = !showNewEntry} title="New entry" disabled={!selectedCategory}>📄+</button>
        <button onclick={loadIndex} title="Refresh">🔄</button>
      </div>
    </div>

    <div class="kb-search">
      <div class="kb-search-input-wrap">
        {#each selectedTags as tag}
          <span class="kb-selected-tag">
            {tag}
            <button onclick={() => toggleTag(tag)}>×</button>
          </span>
        {/each}
        <input
          type="text"
          bind:value={searchQuery}
          placeholder={selectedTags.length > 0 ? 'Add tag...' : 'Search entries...'}
          oninput={() => { if (!searchQuery.trim()) searchResults = null; else doSearch(); }}
          onkeydown={(e) => {
            if (e.key === 'Backspace' && !searchQuery && selectedTags.length > 0) {
              toggleTag(selectedTags[selectedTags.length - 1]);
            }
            if (e.key === 'Escape') clearSearch();
          }}
        />
        {#if tagSuggestions.length > 0}
          <div class="kb-autocomplete">
            {#each tagSuggestions as tag}
              <button class="kb-ac-item" onclick={() => { toggleTag(tag); searchQuery = ''; }}>🏷 {tag}</button>
            {/each}
          </div>
        {/if}
      </div>
      {#if searchQuery || selectedTags.length > 0}
        <button class="kb-clear-search" onclick={clearSearch}>✕</button>
      {/if}
    </div>

    {#if showNewEntry && selectedCategory}
      <div class="kb-new-form">
        <input type="text" bind:value={newEntryName} placeholder="Entry name" onkeydown={(e) => e.key === 'Enter' && createEntry()} />
        <button onclick={createEntry}>Create</button>
        <button onclick={() => showNewEntry = false}>✕</button>
      </div>
    {/if}

    {#if loading}
      <div class="kb-loading">Loading...</div>
    {:else if searchResults !== null}
      {#if searchResults.length === 0}
        <div class="kb-empty">No results found.</div>
      {:else}
        <div class="kb-category">
          <div class="kb-category-header">Search Results ({searchResults.length})</div>
          {#each searchResults as r}
            <div
              class="kb-entry"
              class:active={selectedCategory === r.category && selectedEntry === r.file}
              role="button"
              tabindex="0"
              onclick={() => selectEntry(r.category, r.file)}
              onkeydown={(e) => e.key === 'Enter' && selectEntry(r.category, r.file)}
            >
              <span>📄 {r.title}</span>
              <span class="kb-entry-category">{r.category}</span>
            </div>
          {/each}
        </div>
      {/if}
    {:else if filteredCategories}
      {#if filteredCategories.reduce((sum, cat) => sum + cat.entries.length, 0) === 0}
        <div class="kb-empty">No entries yet. Ask the assistant to research something!</div>
      {:else}
        {#each filteredCategories as cat}
          {#if cat.entries.length > 0}
            <div class="kb-category">
              <div class="kb-category-header">
                <span class="kb-category-name">📂 {cat.name}</span>
                <button class="kb-delete-btn" onclick={() => deleteCategory(cat.name)} title="Delete category">🗑</button>
              </div>
              {#each cat.entries as entry}
                <div
                  class="kb-entry"
                  class:active={selectedCategory === cat.name && selectedEntry === entry.file}
                  role="button"
                  tabindex="0"
                  onclick={() => selectEntry(cat.name, entry.file)}
                  onkeydown={(e) => e.key === 'Enter' && selectEntry(cat.name, entry.file)}
                >
                  <span>📄 {entry.title}</span>
                  <button class="kb-delete-btn" onclick={(e) => { e.stopPropagation(); deleteEntry(cat.name, entry.file); }}>×</button>
                </div>
              {/each}
            </div>
          {:else}
            <div class="kb-category">
              <div class="kb-category-header">
                <span class="kb-category-name">📂 {cat.name}</span>
              </div>
              <div class="kb-empty-cat">No entries yet</div>
            </div>
          {/if}
        {/each}
      {/if}
    {/if}
  </div>

  <div class="kb-content">
    {#if !selectedEntry}
      <div class="kb-placeholder">Select an entry to view, or ask the assistant to research a topic.</div>
    {:else if editing}
      <div class="kb-editor">
        <div class="kb-editor-toolbar">
          <button onclick={saveEdit}>💾 Save</button>
          <button onclick={() => editing = false}>Cancel</button>
        </div>
        <textarea bind:value={editContent}></textarea>
      </div>
    {:else}
      <div class="kb-viewer">
        <div class="kb-viewer-toolbar">
          <span class="kb-entry-title">{selectedEntry?.replace('.md', '')}</span>
          <button onclick={startEdit}>✏️ Edit</button>
        </div>
        <div class="kb-markdown">{@html renderedHtml}</div>
      </div>
    {/if}
  </div>
</div>

<style>
  .knowledge-panel {
    display: flex;
    height: 100%;
    overflow: hidden;
  }

  .kb-sidebar {
    width: 260px;
    min-width: 260px;
    background: var(--bg-secondary, #161b22);
    border-right: 1px solid var(--border, #30363d);
    display: flex;
    flex-direction: column;
    overflow-y: auto;
  }

  .kb-sidebar-header {
    padding: 12px;
    border-bottom: 1px solid var(--border, #30363d);
    display: flex;
    align-items: center;
    justify-content: space-between;
  }

  .kb-search {
    display: flex;
    padding: 8px 12px;
    border-bottom: 1px solid var(--border, #30363d);
    gap: 4px;
  }

  .kb-search-input-wrap {
    flex: 1;
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 4px;
    padding: 4px 8px;
    background: var(--bg-tertiary, #21262d);
    border: 1px solid var(--border, #30363d);
    border-radius: 4px;
    position: relative;
  }

  .kb-search-input-wrap:focus-within {
    border-color: var(--accent, #6366f1);
  }

  .kb-search-input-wrap input {
    flex: 1;
    min-width: 80px;
    padding: 2px 0;
    background: none;
    border: none;
    color: var(--text-primary, #c9d1d9);
    font-size: 12px;
    outline: none;
  }

  .kb-selected-tag {
    display: flex;
    align-items: center;
    gap: 2px;
    padding: 2px 6px;
    background: var(--accent, #6366f1);
    border-radius: 10px;
    color: white;
    font-size: 11px;
  }

  .kb-selected-tag button {
    background: none;
    border: none;
    color: rgba(255, 255, 255, 0.7);
    cursor: pointer;
    font-size: 11px;
    padding: 0 2px;
  }

  .kb-selected-tag button:hover {
    color: white;
  }

  .kb-autocomplete {
    position: absolute;
    top: 100%;
    left: 0;
    right: 0;
    background: var(--bg-secondary, #161b22);
    border: 1px solid var(--border, #30363d);
    border-radius: 0 0 4px 4px;
    z-index: 10;
    max-height: 200px;
    overflow-y: auto;
  }

  .kb-ac-item {
    display: block;
    width: 100%;
    padding: 6px 10px;
    background: none;
    border: none;
    color: var(--text-secondary, #8b949e);
    font-size: 12px;
    text-align: left;
    cursor: pointer;
  }

  .kb-ac-item:hover {
    background: var(--bg-tertiary, #21262d);
    color: var(--text-primary, #c9d1d9);
  }

  .kb-clear-search {
    background: none;
    border: none;
    color: var(--text-secondary, #8b949e);
    cursor: pointer;
    font-size: 12px;
    padding: 4px;
  }

  .kb-clear-search:hover {
    color: var(--text-primary, #c9d1d9);
  }

  .kb-entry-category {
    font-size: 10px;
    color: var(--text-secondary, #8b949e);
    opacity: 0.6;
  }

  .kb-sidebar-header h3 {
    font-size: 14px;
    font-weight: 600;
    color: var(--text-primary, #c9d1d9);
  }

  .kb-actions {
    display: flex;
    gap: 4px;
  }

  .kb-actions button {
    background: none;
    border: 1px solid var(--border, #30363d);
    border-radius: 4px;
    color: var(--text-secondary, #8b949e);
    cursor: pointer;
    padding: 2px 6px;
    font-size: 12px;
  }

  .kb-actions button:hover {
    border-color: var(--accent, #6366f1);
    color: var(--text-primary, #c9d1d9);
  }

  .kb-actions button:disabled {
    opacity: 0.4;
    cursor: not-allowed;
  }

  .kb-new-form {
    display: flex;
    gap: 4px;
    padding: 8px 12px;
    border-bottom: 1px solid var(--border, #30363d);
  }

  .kb-new-form input {
    flex: 1;
    padding: 4px 8px;
    background: var(--bg-tertiary, #21262d);
    border: 1px solid var(--border, #30363d);
    border-radius: 4px;
    color: var(--text-primary, #c9d1d9);
    font-size: 12px;
  }

  .kb-new-form button {
    padding: 4px 8px;
    background: var(--accent, #6366f1);
    border: none;
    border-radius: 4px;
    color: white;
    font-size: 12px;
    cursor: pointer;
  }

  .kb-loading, .kb-empty {
    padding: 16px;
    color: var(--text-secondary, #8b949e);
    font-size: 13px;
  }

  .kb-category {
    border-bottom: 1px solid var(--border, #30363d);
  }

  .kb-category-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 8px 12px;
    font-size: 13px;
    font-weight: 600;
    color: var(--text-primary, #c9d1d9);
  }

  .kb-entry {
    display: flex;
    align-items: center;
    justify-content: space-between;
    width: 100%;
    padding: 6px 12px 6px 24px;
    background: none;
    border: none;
    color: var(--text-secondary, #8b949e);
    font-size: 12px;
    cursor: pointer;
    text-align: left;
  }

  .kb-entry:hover {
    background: var(--bg-tertiary, #21262d);
    color: var(--text-primary, #c9d1d9);
  }

  .kb-entry.active {
    background: rgba(99, 102, 241, 0.15);
    color: var(--text-primary, #c9d1d9);
  }

  .kb-delete-btn {
    background: none;
    border: none;
    color: var(--text-secondary, #8b949e);
    cursor: pointer;
    font-size: 11px;
    padding: 2px 4px;
    opacity: 0.5;
  }

  .kb-delete-btn:hover {
    opacity: 1;
    color: #f97583;
  }

  .kb-empty-cat {
    padding: 6px 24px;
    color: var(--text-secondary, #8b949e);
    font-size: 11px;
    opacity: 0.5;
    font-style: italic;
  }

  .kb-content {
    flex: 1;
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  .kb-placeholder {
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--text-secondary, #8b949e);
    font-size: 14px;
  }

  .kb-viewer, .kb-editor {
    display: flex;
    flex-direction: column;
    height: 100%;
  }

  .kb-viewer-toolbar, .kb-editor-toolbar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 8px 16px;
    border-bottom: 1px solid var(--border, #30363d);
  }

  .kb-entry-title {
    font-weight: 600;
    font-size: 14px;
    color: var(--text-primary, #c9d1d9);
  }

  .kb-viewer-toolbar button, .kb-editor-toolbar button {
    padding: 4px 12px;
    background: var(--bg-tertiary, #21262d);
    border: 1px solid var(--border, #30363d);
    border-radius: 4px;
    color: var(--text-secondary, #8b949e);
    cursor: pointer;
    font-size: 12px;
  }

  .kb-viewer-toolbar button:hover, .kb-editor-toolbar button:hover {
    border-color: var(--accent, #6366f1);
    color: var(--text-primary, #c9d1d9);
  }

  .kb-markdown {
    flex: 1;
    overflow-y: auto;
    padding: 16px;
    line-height: 1.6;
    font-size: 14px;
    color: var(--text-primary, #c9d1d9);
  }

  .kb-markdown :global(h1) { font-size: 20px; margin-bottom: 12px; }
  .kb-markdown :global(h2) { font-size: 16px; margin: 16px 0 8px; }
  .kb-markdown :global(h3) { font-size: 14px; margin: 12px 0 6px; }
  .kb-markdown :global(p) { margin-bottom: 8px; }
  .kb-markdown :global(ul), .kb-markdown :global(ol) { margin-bottom: 8px; padding-left: 20px; }
  .kb-markdown :global(code) { background: var(--bg-tertiary, #21262d); padding: 2px 6px; border-radius: 3px; font-size: 13px; }
  .kb-markdown :global(pre) { background: var(--bg-tertiary, #21262d); padding: 12px; border-radius: 6px; overflow-x: auto; margin-bottom: 12px; }
  .kb-markdown :global(a) { color: var(--accent, #6366f1); }
  .kb-markdown :global(table) { border-collapse: collapse; margin-bottom: 12px; }
  .kb-markdown :global(th), .kb-markdown :global(td) { border: 1px solid var(--border, #30363d); padding: 6px 12px; font-size: 13px; }

  .kb-editor textarea {
    flex: 1;
    background: var(--bg-primary, #0d1117);
    color: var(--text-primary, #c9d1d9);
    border: none;
    padding: 16px;
    font-family: 'Cascadia Code', 'Fira Code', monospace;
    font-size: 13px;
    line-height: 1.6;
    resize: none;
    outline: none;
  }
</style>
