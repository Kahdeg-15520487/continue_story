<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';

  let { slug }: { slug: string } = $props();

  let files: string[] = $state([]);
  let activeFile: string | null = $state(null);
  let content = $state('');
  let loading = $state(false);
  let generating = $state(false);

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
    } catch (err: any) {
      content = `Error loading file: ${err.message}`;
    } finally {
      loading = false;
    }
  }

  async function generate() {
    generating = true;
    try {
      await api.triggerLoreGeneration(slug);
      const interval = setInterval(async () => {
        await loadFiles();
        if (files.length > 0) {
          clearInterval(interval);
          generating = false;
          await loadFile(files[0]);
        }
      }, 5000);
    } catch (err) {
      console.error('Lore generation failed:', err);
      generating = false;
    }
  }

  onMount(loadFiles);
</script>

<div class="lore-panel">
  <h3 class="panel-title">Wiki</h3>

  <div class="lore-actions">
    <button class="btn btn-primary" onclick={generate} disabled={generating}>
      {generating ? 'Generating...' : 'Generate Lore'}
    </button>
  </div>

  <div class="file-tabs">
    {#each files as file}
      <button
        class="file-tab"
        class:active={activeFile === file}
        onclick={() => loadFile(file)}
      >
        {file}
      </button>
    {/each}
  </div>

  <div class="lore-content">
    {#if activeFile && loading}
      <p class="loading">Loading...</p>
    {:else if activeFile}
      <pre>{content}</pre>
    {:else if files.length === 0}
      <p class="empty-hint">No lore generated yet. Click "Generate Lore" to analyze the book.</p>
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

  .lore-actions {
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
  }

  .btn {
    padding: 6px 14px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 13px;
  }

  .btn-primary {
    background: #238636;
    border-color: #238636;
    color: white;
  }

  .btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .file-tabs {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    padding: 8px 16px;
    border-bottom: 1px solid var(--border);
  }

  .file-tab {
    padding: 4px 10px;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: transparent;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 12px;
  }

  .file-tab.active {
    background: var(--bg-tertiary);
    color: var(--text-primary);
  }

  .lore-content {
    flex: 1;
    overflow-y: auto;
    padding: 16px;
  }

  .lore-content pre {
    font-size: 13px;
    line-height: 1.6;
    white-space: pre-wrap;
    word-break: break-word;
    color: var(--text-primary);
  }

  .loading, .empty-hint {
    color: var(--text-secondary);
    font-size: 13px;
    text-align: center;
    padding-top: 24px;
  }
</style>
