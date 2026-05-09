<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { marked } from 'marked';

  interface KBEntry { file: string; title: string; }
  interface KBCategory { name: string; entries: KBEntry[]; }

  let categories = $state<KBCategory[]>([]);
  let selectedCategory = $state<string | null>(null);
  let selectedEntry = $state<string | null>(null);
  let entryContent = $state('');
  let loading = $state(true);
  let editing = $state(false);
  let editContent = $state('');
  let newCategoryName = $state('');
  let showNewCategory = $state(false);
  let showNewEntry = $state(false);
  let newEntryName = $state('');

  async function loadIndex() {
    try {
      const data = await api.getKnowledgeIndex();
      categories = data.categories;
    } catch (err) {
      console.error('Failed to load KB index:', err);
    } finally {
      loading = false;
    }
  }

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

  async function createCategory() {
    if (!newCategoryName.trim()) return;
    try {
      await api.createKnowledgeCategory(newCategoryName.trim());
      newCategoryName = '';
      showNewCategory = false;
      await loadIndex();
    } catch (err) {
      console.error('Failed to create category:', err);
    }
  }

  async function createEntry() {
    if (!selectedCategory || !newEntryName.trim()) return;
    const name = newEntryName.trim().endsWith('.md') ? newEntryName.trim() : newEntryName.trim() + '.md';
    const title = name.replace('.md', '');
    try {
      await api.saveKnowledgeEntry(selectedCategory, name, `# ${title}\n\n`);
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

  let renderedHtml = $derived(entryContent ? marked.parse(entryContent, { async: false }) as string : '');

  onMount(loadIndex);
</script>

<div class="knowledge-panel">
  <div class="kb-sidebar">
    <div class="kb-sidebar-header">
      <h3>Knowledge Base</h3>
      <div class="kb-actions">
        <button onclick={() => showNewCategory = !showNewCategory} title="New category">📁+</button>
        <button onclick={() => showNewEntry = !showNewEntry} title="New entry" disabled={!selectedCategory}>📄+</button>
        <button onclick={loadIndex} title="Refresh">🔄</button>
      </div>
    </div>

    {#if showNewCategory}
      <div class="kb-new-form">
        <input type="text" bind:value={newCategoryName} placeholder="Category name" onkeydown={(e) => e.key === 'Enter' && createCategory()} />
        <button onclick={createCategory}>Create</button>
        <button onclick={() => showNewCategory = false}>✕</button>
      </div>
    {/if}

    {#if showNewEntry && selectedCategory}
      <div class="kb-new-form">
        <input type="text" bind:value={newEntryName} placeholder="Entry name" onkeydown={(e) => e.key === 'Enter' && createEntry()} />
        <button onclick={createEntry}>Create</button>
        <button onclick={() => showNewEntry = false}>✕</button>
      </div>
    {/if}

    {#if loading}
      <div class="kb-loading">Loading...</div>
    {:else if categories.length === 0}
      <div class="kb-empty">No entries yet. Ask the assistant to research something!</div>
    {:else}
      {#each categories as cat}
        <div class="kb-category">
          <div class="kb-category-header">
            <span class="kb-category-name">📂 {cat.name}</span>
            <button class="kb-delete-btn" onclick={() => deleteCategory(cat.name)} title="Delete category">🗑</button>
          </div>
          {#each cat.entries as entry}
            <button
              class="kb-entry"
              class:active={selectedCategory === cat.name && selectedEntry === entry.file}
              onclick={() => selectEntry(cat.name, entry.file)}
            >
              <span>📄 {entry.title}</span>
              <button class="kb-delete-btn" onclick|stopPropagation={() => deleteEntry(cat.name, entry.file)}>×</button>
            </button>
          {/each}
        </div>
      {/each}
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
        <textarea bind:value={editContent} />
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
