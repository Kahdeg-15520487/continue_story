<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { renderMarkdown } from '$lib/markdown';
  import Icon from '$lib/components/Icon.svelte';
  import type { WikiCategory, WikiEntity } from '$lib/types';

  let { slug }: { slug: string } = $props();

  let categories: WikiCategory[] = $state([]);
  let hasSummary = $state(false);
  let selectedCategory: string | null = $state(null);
  let selectedEntity: string | null = $state(null);
  let content = $state('');
  let renderedHtml = $state('');
  let loading = $state(false);
  let generating = $state(false);
  let wikiError = $state('');
  let editing = $state(false);
  let editContent = $state('');
  let savingEdit = $state(false);
  let filter = $state('');

  let filteredCategories = $derived.by(() => {
    const q = filter.trim().toLowerCase();
    if (!q) return categories;
    return categories
      .map(cat => ({ ...cat, entities: cat.entities.filter(e => e.name.toLowerCase().includes(q)) }))
      .filter(cat => cat.entities.length > 0);
  });

  const categoryIcons: Record<string, string> = {
    characters: '👤',
    locations: '📍',
  };

  async function loadIndex() {
    try {
      const result = await api.getWikiIndex(slug);
      categories = result.categories;
      hasSummary = result.hasSummary;
    } catch {
      categories = [];
      hasSummary = false;
    }
  }

  async function selectEntity(cat: string, entity: WikiEntity) {
    if (editing && !confirm('Discard unsaved changes?')) return;
    editing = false;
    loading = true;
    selectedCategory = cat;
    selectedEntity = entity.file;
    content = '';
    renderedHtml = '';
    wikiError = '';
    try {
      const parts = entity.file.split('/');
      const result = await api.getWikiEntity(slug, parts[0], parts[1]);
      content = result.content;
      renderedHtml = renderMarkdown(content);
    } catch (err: any) {
      wikiError = `Failed to load entity: ${err.message}`;
      renderedHtml = '';
      content = '';
    } finally {
      loading = false;
    }
  }

  async function selectSummary() {
    if (editing && !confirm('Discard unsaved changes?')) return;
    editing = false;
    loading = true;
    selectedCategory = 'summary';
    selectedEntity = 'summary';
    content = '';
    renderedHtml = '';
    wikiError = '';
    try {
      const result = await api.getWikiSummary(slug);
      content = result.content;
      renderedHtml = renderMarkdown(content);
    } catch (err: any) {
      wikiError = `Failed to load summary: ${err.message}`;
      renderedHtml = '';
      content = '';
    } finally {
      loading = false;
    }
  }

  async function generate() {
    generating = true;
    wikiError = '';
    try {
      await api.triggerLoreGeneration(slug);
      let attempts = 0;
      const maxAttempts = 24;
      const interval = setInterval(async () => {
        attempts++;
        await loadIndex();
        if (categories.some(c => c.entities.length > 0) || hasSummary) {
          clearInterval(interval);
          generating = false;
        } else if (attempts >= maxAttempts) {
          clearInterval(interval);
          generating = false;
          wikiError = 'Wiki generation timed out.';
        }
      }, 5000);
    } catch (err: any) {
      wikiError = err.message || 'Wiki generation failed';
      generating = false;
    }
  }

  onMount(loadIndex);

  function startEdit() {
    if (!selectedCategory || !selectedEntity) return;
    editContent = content;
    editing = true;
  }

  async function saveEdit() {
    if (!selectedCategory || !selectedEntity || savingEdit) return;
    savingEdit = true;
    try {
      const [cat, file] = selectedEntity === 'summary' ? ['root', 'summary.md'] : selectedEntity.split('/');
      await api.saveWikiEntity(slug, cat, file, editContent);
      content = editContent;
      renderedHtml = renderMarkdown(content);
      editing = false;
      await refresh();
    } catch (err: any) {
      wikiError = `Failed to save: ${err.message}`;
    } finally {
      savingEdit = false;
    }
  }

  async function refresh() {
    await loadIndex();
  }

  export { refresh };
</script>

<div class="wiki-panel">
  <div class="panel-header">
    <div class="panel-heading">
      <span class="assistant-badge"><Icon name="wiki" size={15} /></span>
      <div class="panel-title-group">
        <h3 class="panel-title">Wiki</h3>
        <p class="panel-sub">People, places, lore</p>
      </div>
    </div>
    {#if categories.length === 0 && !hasSummary}
      <button class="btn-generate" onclick={generate} disabled={generating}>
        <Icon name="sparkles" size={14} /> {generating ? 'Generating…' : 'Generate'}
      </button>
    {/if}
  </div>

  <div class="wiki-body">
    {#if categories.length === 0 && !hasSummary && !generating}
      <div class="empty-state">
        <span class="empty-icon"><Icon name="wiki" size={26} /></span>
        <p>No wiki yet</p>
        <small>Generate a wiki to collect the people, places, and lore of this book.</small>
      </div>
    {:else}
      <!-- Left column: entity list -->
      <div class="wiki-list">
        {#if categories.length > 0}
          <input
            class="wiki-filter"
            type="text"
            placeholder="Search..."
            bind:value={filter}
            onkeydown={(e) => { if (e.key === 'Escape') filter = ''; }}
          />
        {/if}
        {#each filteredCategories as cat}
          {#if cat.entities.length > 0}
            <div class="category">
              <div class="category-header">
                <span class="category-icon">{categoryIcons[cat.name] || '📁'}</span>
                <span class="category-label">{cat.label}</span>
                <span class="category-count">{cat.entities.length}</span>
              </div>
              {#each cat.entities as entity}
                <button
                  class="entity-item"
                  class:active={selectedEntity === entity.file}
                  onclick={() => selectEntity(cat.name, entity)}
                  title={entity.name}
                >
                  {entity.name}
                </button>
              {/each}
            </div>
          {/if}
        {/each}

        {#if hasSummary}
          <button
            class="entity-item summary-item"
            class:active={selectedEntity === 'summary'}
            onclick={selectSummary}
          >
            📖 Plot Summary
          </button>
        {/if}
      </div>

      <!-- Right column: entity detail -->
      <div class="wiki-detail">
        {#if editing && selectedEntity}
          <div class="wiki-editor">
            <div class="wiki-editor-toolbar">
              <button class="btn-edit" onclick={saveEdit} disabled={savingEdit}>{savingEdit ? 'Saving...' : '💾 Save'}</button>
              <button class="btn-edit" onclick={() => editing = false}>Cancel</button>
            </div>
            <textarea bind:value={editContent}></textarea>
          </div>
        {:else}
          {#if loading}
            <p class="loading">Loading...</p>
          {:else if renderedHtml}
            <div class="wiki-rendered">
              <div class="wiki-toolbar">
                <button class="btn-edit" onclick={startEdit} title="Edit raw markdown">✏️ Edit</button>
              </div>
              {@html renderedHtml}
            </div>
          {:else if selectedEntity}
            <p class="loading">Select an entity to view.</p>
          {:else}
            <p class="loading">Select an entity from the list.</p>
          {/if}
        {/if}
      </div>
    {/if}

    {#if wikiError}
      <div class="wiki-error">{wikiError}</div>
    {/if}

    {#if generating}
      <div class="generating-banner">
        <span class="spinner"></span> Generating wiki entities...
      </div>
    {/if}
  </div>
</div>

<style>
  .wiki-panel {
    display: flex;
    flex-direction: column;
    height: 100%;
  }

  .panel-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 10px;
    padding: 13px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-elevated);
  }

  .panel-heading {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
  }

  .assistant-badge {
    width: 32px;
    height: 32px;
    border-radius: 10px;
    background: var(--accent-soft);
    color: var(--accent);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .panel-title {
    font-size: 15px;
    font-weight: 600;
    margin: 0;
    line-height: 1.15;
  }

  .panel-sub {
    font-size: 10.5px;
    color: var(--text-muted);
    margin-top: 1px;
  }

  .btn-generate {
    height: 32px;
    padding: 0 13px;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font-size: 12px;
    font-weight: 500;
    border: none;
    border-radius: var(--r-full);
    background: var(--accent);
    color: #fff;
    cursor: pointer;
  }

  .btn-generate:hover:not(:disabled) {
    background: var(--accent-hover);
  }

  .btn-generate:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .wiki-body {
    flex: 1;
    display: flex;
    overflow: hidden;
  }

  /* Left column */
  .wiki-list {
    width: 160px;
    min-width: 120px;
    border-right: 1px solid var(--border);
    overflow-y: auto;
    padding: 8px 0;
  }

  .wiki-filter {
    width: calc(100% - 24px);
    height: 34px;
    margin: 0 12px 10px;
    padding: 0 10px;
    background: var(--bg-elevated);
    border: 1px solid var(--border);
    border-radius: 9px;
    color: var(--text-primary);
    font-size: 12.5px;
    outline: none;
  }

  .wiki-filter:focus {
    border-color: var(--accent);
  }

  .category {
    margin-bottom: 8px;
  }

  .category-header {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 6px 12px;
    font-size: 11px;
    font-weight: 600;
    color: var(--text-secondary);
    text-transform: uppercase;
    letter-spacing: 0.5px;
  }

  .category-icon {
    font-size: 13px;
  }

  .category-count {
    font-size: 10px;
    background: var(--bg-tertiary);
    padding: 1px 6px;
    border-radius: 8px;
    color: var(--text-secondary);
  }

  .entity-item {
    display: block;
    width: 100%;
    text-align: left;
    padding: 5px 12px 5px 28px;
    background: none;
    border: none;
    color: var(--text-primary);
    font-size: 12px;
    cursor: pointer;
    transition: background 0.1s;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .entity-item:hover {
    background: var(--bg-tertiary);
  }

  .entity-item.active {
    background: var(--bg-tertiary);
    border-left: 2px solid var(--accent);
    padding-left: 26px;
  }

  .summary-item {
    padding-left: 12px;
    margin-top: 4px;
    border-top: 1px solid var(--border);
    padding-top: 8px;
  }

  /* Right column */
  .wiki-detail {
    flex: 1;
    overflow-y: auto;
  }

  .wiki-rendered {
    padding: 16px 20px;
    font-size: 13px;
    line-height: 1.65;
    color: var(--text-primary);
  }

  .wiki-rendered :global(h1) {
    font-size: 20px;
    font-weight: 700;
    margin: 0 0 12px 0;
    padding-bottom: 8px;
    border-bottom: 2px solid var(--border);
  }

  .wiki-rendered :global(h2) {
    font-size: 15px;
    font-weight: 600;
    margin: 20px 0 8px 0;
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border-radius: 6px;
    border-left: 3px solid var(--accent);
  }

  .wiki-rendered :global(p) { margin: 6px 0; }
  .wiki-rendered :global(strong) { color: var(--accent-hover); font-weight: 600; }
  .wiki-rendered :global(blockquote) {
    margin: 8px 0; padding: 6px 12px; border-left: 3px solid var(--border);
    color: var(--text-secondary); font-size: 12px; background: rgba(110, 118, 129, 0.05); border-radius: 0 4px 4px 0;
  }
  .wiki-rendered :global(ul) { margin: 4px 0; padding-left: 20px; }
  .wiki-rendered :global(li) { margin: 3px 0; }
  .wiki-rendered :global(li)::marker { color: var(--accent); }

  .loading, .empty-hint {
    color: var(--text-secondary);
    font-size: 13px;
    text-align: center;
    padding-top: 24px;
  }

  .wiki-error {
    background: var(--error-bg);
    color: var(--error);
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 12px;
    margin: 12px 16px;
  }

  .wiki-toolbar {
    display: flex;
    justify-content: flex-end;
    margin-bottom: 8px;
  }

  .wiki-editor {
    flex: 1;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    height: 100%;
  }

  .wiki-editor-toolbar {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 8px 16px;
    border-bottom: 1px solid var(--border);
  }

  .wiki-editor textarea {
    flex: 1;
    background: var(--bg-primary);
    color: var(--text-primary);
    border: none;
    padding: 16px;
    font-family: 'Cascadia Code', 'Fira Code', monospace;
    font-size: 13px;
    line-height: 1.6;
    resize: none;
    outline: none;
  }

  .btn-edit {
    padding: 4px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 4px;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 12px;
  }

  .btn-edit:hover:not(:disabled) {
    border-color: var(--accent);
    color: var(--text-primary);
  }

  .btn-edit:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .generating-banner {
    display: flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 12px;
    color: var(--text-secondary);
    font-size: 12px;
  }

  .spinner {
    display: inline-block;
    width: 14px;
    height: 14px;
    border: 2px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin { to { transform: rotate(360deg); } }

  .entity-item {
    width: calc(100% - 16px);
    margin: 1px 8px;
    padding: 7px 12px 7px 26px;
    border-radius: 8px;
    font-size: 12.5px;
  }

  .entity-item:hover {
    background: var(--bg-tertiary);
  }

  .entity-item.active {
    background: var(--accent-soft);
    color: var(--accent);
  }

  .category-header {
    padding: 10px 14px 4px;
    font-size: 10px;
    letter-spacing: 0.08em;
  }

  .empty-state {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 8px;
    padding: 32px 24px;
    text-align: center;
  }

  .empty-icon {
    width: 52px;
    height: 52px;
    border-radius: 16px;
    background: var(--accent-soft);
    color: var(--accent);
    display: flex;
    align-items: center;
    justify-content: center;
    margin-bottom: 4px;
  }

  .empty-state p {
    font-family: var(--font-serif);
    font-size: 18px;
    color: var(--text-primary);
  }

  .empty-state small {
    max-width: 280px;
    font-size: 12.5px;
    line-height: 1.6;
    color: var(--text-secondary);
  }
</style>
