<script lang="ts">
  import { onMount } from 'svelte';
  import { marked } from 'marked';
  import { api } from '$lib/api';

  let { slug }: { slug: string } = $props();

  let files: string[] = $state([]);
  let activeFile: string | null = $state(null);
  let content = $state('');
  let renderedHtml = $state('');
  let loading = $state(false);
  let generating = $state(false);
  let loreError = $state('');

  const fileLabels: Record<string, string> = {
    'characters.md': 'Characters',
    'locations.md': 'Locations',
    'themes.md': 'Themes',
    'summary.md': 'Summary',
    'chapter-summaries.md': 'Chapters',
  };

  const fileIcons: Record<string, string> = {
    'characters.md': '👤',
    'locations.md': '📍',
    'themes.md': '💡',
    'summary.md': '📖',
    'chapter-summaries.md': '📑',
  };

  function renderMarkdown(md: string) {
    renderedHtml = marked.parse(md, { async: false }) as string;
  }

  async function loadFiles() {
    try {
      const result = await api.getLoreFiles(slug);
      files = result.files;
    } catch {
      files = [];
    }
  }

  async function loadFile(file: string) {
    loading = true;
    activeFile = file;
    try {
      const result = await api.getLoreContent(slug, file);
      content = result.content;
      renderMarkdown(content);
    } catch (err: any) {
      content = `Error loading file: ${err.message}`;
      renderedHtml = '';
    } finally {
      loading = false;
    }
  }

  async function generate() {
    generating = true;
    loreError = '';
    try {
      await api.triggerLoreGeneration(slug);
      let attempts = 0;
      const maxAttempts = 24;
      const interval = setInterval(async () => {
        attempts++;
        await loadFiles();
        if (files.length > 0) {
          clearInterval(interval);
          generating = false;
          await loadFile(files[0]);
        } else if (attempts >= maxAttempts) {
          clearInterval(interval);
          generating = false;
          loreError = 'Lore generation timed out. The agent may not have API keys configured.';
        }
      }, 5000);
    } catch (err: any) {
      loreError = err.message || 'Lore generation failed';
      generating = false;
    }
  }

  onMount(loadFiles);
</script>

<div class="lore-panel">
  <h3 class="panel-title">Wiki</h3>

  <div class="file-tabs">
    {#each files as file}
      <button
        class="file-tab"
        class:active={activeFile === file}
        onclick={() => loadFile(file)}
        title={fileLabels[file] || file}
      >
        <span class="tab-icon">{fileIcons[file] || '📄'}</span>
        <span class="tab-label">{fileLabels[file] || file.replace('.md', '')}</span>
      </button>
    {/each}
  </div>

  <div class="lore-content">
    {#if activeFile && loading}
      <p class="loading">Loading...</p>
    {:else if activeFile && renderedHtml}
      <div class="wiki-rendered">{@html renderedHtml}</div>
    {:else if files.length === 0}
      <p class="empty-hint">No lore generated yet. Click "Generate Lore" to analyze the book.</p>
    {/if}

    {#if loreError}
      <div class="lore-error">{loreError}</div>
    {/if}
  </div>
</div>

<style>
  .lore-panel {
    display: flex;
    flex-direction: column;
    height: 100%;
  }

  .panel-title {
    padding: 12px 16px;
    font-size: 14px;
    font-weight: 600;
    border-bottom: 1px solid var(--border);
  }

  .file-tabs {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    padding: 8px 16px;
    border-bottom: 1px solid var(--border);
  }

  .file-tab {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 4px 10px;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: transparent;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 12px;
    transition: all 0.15s;
  }

  .file-tab:hover {
    background: var(--bg-tertiary);
    color: var(--text-primary);
  }

  .file-tab.active {
    background: var(--bg-tertiary);
    color: var(--text-primary);
    border-color: var(--text-secondary);
  }

  .tab-icon {
    font-size: 13px;
  }

  .tab-label {
    font-size: 12px;
  }

  .lore-content {
    flex: 1;
    overflow-y: auto;
    padding: 0;
  }

  /* Wiki rendered markdown */
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
    color: var(--text-primary);
  }

  .wiki-rendered :global(h2) {
    font-size: 15px;
    font-weight: 600;
    margin: 20px 0 8px 0;
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border-radius: 6px;
    border-left: 3px solid #58a6ff;
    color: var(--text-primary);
  }

  .wiki-rendered :global(h3) {
    font-size: 14px;
    font-weight: 600;
    margin: 16px 0 6px 0;
    color: var(--text-primary);
  }

  .wiki-rendered :global(p) {
    margin: 6px 0;
  }

  .wiki-rendered :global(strong) {
    color: #79c0ff;
    font-weight: 600;
  }

  .wiki-rendered :global(em) {
    color: var(--text-secondary);
    font-style: italic;
  }

  .wiki-rendered :global(blockquote) {
    margin: 8px 0;
    padding: 6px 12px;
    border-left: 3px solid var(--border);
    color: var(--text-secondary);
    font-size: 12px;
    background: rgba(110, 118, 129, 0.05);
    border-radius: 0 4px 4px 0;
  }

  .wiki-rendered :global(ul) {
    margin: 4px 0;
    padding-left: 20px;
  }

  .wiki-rendered :global(li) {
    margin: 3px 0;
  }

  .wiki-rendered :global(li)::marker {
    color: #58a6ff;
  }

  .wiki-rendered :global(hr) {
    border: none;
    border-top: 1px solid var(--border);
    margin: 16px 0;
  }

  .wiki-rendered :global(code) {
    background: var(--bg-tertiary);
    padding: 1px 5px;
    border-radius: 3px;
    font-size: 12px;
  }

  .loading, .empty-hint {
    color: var(--text-secondary);
    font-size: 13px;
    text-align: center;
    padding-top: 24px;
  }

  .lore-error {
    background: #3d1f1f;
    color: #f97583;
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 12px;
    margin: 12px 16px;
  }
</style>
