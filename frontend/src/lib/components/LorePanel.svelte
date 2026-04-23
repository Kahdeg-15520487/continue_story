<script lang="ts">
  import { onMount } from 'svelte';
  import { marked } from 'marked';
  import { api } from '$lib/api';
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

  const categoryIcons: Record<string, string> = {
    characters: '👤',
    locations: '📍',
  };

  function renderMarkdown(md: string) {
    renderedHtml = marked.parse(md, { async: false }) as string;
  }

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
    loading = true;
    selectedCategory = cat;
    selectedEntity = entity.file;
    content = '';
    renderedHtml = '';
    try {
      const parts = entity.file.split('/');
      const result = await api.getWikiEntity(slug, parts[0], parts[1]);
      content = result.content;
      renderMarkdown(content);
    } catch (err: any) {
      content = `Error: ${err.message}`;
      renderedHtml = '';
    } finally {
      loading = false;
    }
  }

  async function selectSummary() {
    loading = true;
    selectedCategory = 'summary';
    selectedEntity = 'summary';
    content = '';
    renderedHtml = '';
    try {
      const result = await api.getWikiSummary(slug);
      content = result.content;
      renderMarkdown(content);
    } catch (err: any) {
      content = `Error: ${err.message}`;
      renderedHtml = '';
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
</script>

<div class="wiki-panel">
  <div class="panel-header">
    <h3 class="panel-title">Wiki</h3>
    {#if categories.length === 0 && !hasSummary}
      <button class="btn-generate" onclick={generate} disabled={generating}>
        {generating ? 'Generating...' : 'Generate Wiki'}
      </button>
    {/if}
  </div>

  <div class="wiki-body">
    {#if categories.length === 0 && !hasSummary && !generating}
      <p class="empty-hint">No wiki generated yet. Click "Generate Wiki" to analyze the book.</p>
    {:else}
      <!-- Left column: entity list -->
      <div class="wiki-list">
        {#each categories as cat}
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
        {#if loading}
          <p class="loading">Loading...</p>
        {:else if renderedHtml}
          <div class="wiki-rendered">{@html renderedHtml}</div>
        {:else if selectedEntity}
          <p class="loading">Select an entity to view.</p>
        {:else}
          <p class="loading">Select an entity from the list.</p>
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
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
  }

  .panel-title {
    font-size: 14px;
    font-weight: 600;
    margin: 0;
  }

  .btn-generate {
    padding: 4px 12px;
    font-size: 11px;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    cursor: pointer;
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
    border-left: 2px solid #58a6ff;
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
    border-left: 3px solid #58a6ff;
  }

  .wiki-rendered :global(p) { margin: 6px 0; }
  .wiki-rendered :global(strong) { color: #79c0ff; font-weight: 600; }
  .wiki-rendered :global(blockquote) {
    margin: 8px 0; padding: 6px 12px; border-left: 3px solid var(--border);
    color: var(--text-secondary); font-size: 12px; background: rgba(110, 118, 129, 0.05); border-radius: 0 4px 4px 0;
  }
  .wiki-rendered :global(ul) { margin: 4px 0; padding-left: 20px; }
  .wiki-rendered :global(li) { margin: 3px 0; }
  .wiki-rendered :global(li)::marker { color: #58a6ff; }

  .loading, .empty-hint {
    color: var(--text-secondary);
    font-size: 13px;
    text-align: center;
    padding-top: 24px;
  }

  .wiki-error {
    background: #3d1f1f;
    color: #f97583;
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 12px;
    margin: 12px 16px;
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
</style>
