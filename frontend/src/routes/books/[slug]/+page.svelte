<script lang="ts">
  import { page } from '$app/stores';
  import { onMount } from 'svelte';
  import BookEditor from '$lib/components/BookEditor.svelte';
  import UploadZone from '$lib/components/UploadZone.svelte';
  import ChatPanel from '$lib/components/ChatPanel.svelte';
  import LorePanel from '$lib/components/LorePanel.svelte';
  import { api } from '$lib/api';
  import type { BookDetail } from '$lib/types';

  let slug = $derived($page.params.slug);
  let book: BookDetail | null = $state(null);
  let content = $state('');
  let loading = $state(true);
  let error = $state('');
  let isEditing = $state(false);
  let saving = $state(false);
  let showChat = $state(false);
  let showLore = $state(false);

  function handleUploaded(result: { sourceFile: string; status: string }) {
    loadBook();
    // Poll for conversion completion
    const poll = setInterval(async () => {
      if (!slug) { clearInterval(poll); return; }
      try {
        const updated = await api.getBook(slug);
        if (updated) {
          book = updated;
          if (updated.status === 'ready') {
            clearInterval(poll);
            await loadBook();
          } else if (updated.status === 'error') {
            clearInterval(poll);
          }
        }
      } catch {
        clearInterval(poll);
      }
    }, 3000);
  }

  let saveTimeout: ReturnType<typeof setTimeout> | null = null;

  async function debouncedSave(newContent: string) {
    if (saveTimeout) clearTimeout(saveTimeout);
    saveTimeout = setTimeout(async () => {
      await saveContent(newContent);
    }, 1000);
  }

  async function loadBook() {
    loading = true;
    error = '';
    try {
      book = await api.getBook(slug);
      if (book.status === 'ready') {
        const result = await api.getBookContent(slug);
        content = result.content;
      }
    } catch (err: any) {
      error = err.message;
    } finally {
      loading = false;
    }
  }

  async function saveContent(newContent: string) {
    if (saving || !isEditing) return;
    saving = true;
    try {
      await api.saveBookContent(slug, newContent);
      content = newContent;
    } catch (err) {
      console.error('Save failed:', err);
    } finally {
      saving = false;
    }
  }

  onMount(loadBook);
</script>

{#if loading}
  <div class="loading-screen">Loading book...</div>
{:else if error}
  <div class="error-screen">
    <p>{error}</p>
    <a href="/" class="back-link">← Back to Library</a>
  </div>
{:else if book}
  <div class="book-view">
    <div class="toolbar">
      <a href="/" class="back-link">← Library</a>
      <h2 class="book-title">{book.title}</h2>
      <div class="toolbar-actions">
          <button class="btn" onclick={() => isEditing = !isEditing}>
            {isEditing ? '🔒 Lock' : '✏️ Edit'}
          </button>
          {#if saving}
            <span class="status-saving">Saving...</span>
          {/if}
        <button class="btn" onclick={() => showLore = !showLore}>
          📖 Wiki
        </button>
        <button class="btn" onclick={() => showChat = !showChat}>
          💬 Chat
        </button>
      </div>
    </div>

    <div class="main-area">
      <div class="editor-pane">
        {#if book.status === 'ready'}
          <BookEditor
            bind:content
            readonly={!isEditing}
            onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
          />
        {:else if book.status === 'pending'}
          <div class="status-section">
            <p class="status-message">Upload a file to convert to markdown.</p>
            <UploadZone {slug} onUploaded={handleUploaded} />
          </div>
        {:else if book.status === 'converting'}
          <div class="status-section">
            <p class="status-message converting">Converting to markdown...</p>
            <div class="spinner"></div>
            <button class="btn-secondary" onclick={loadBook}>Refresh</button>
          </div>
        {:else}
          <div class="status-section">
            <p class="status-message error">
              Conversion failed{book.errorMessage ? `: ${book.errorMessage}` : ''}
            </p>
            <UploadZone {slug} onUploaded={handleUploaded} />
          </div>
        {/if}
      </div>

      {#if showLore}
        <div class="side-panel">
          <LorePanel {slug} />
        </div>
      {/if}

      {#if showChat}
        <div class="side-panel">
          <ChatPanel {slug} />
        </div>
      {/if}
    </div>
  </div>
{/if}

<style>
  .loading-screen, .error-screen {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100vh;
    gap: 16px;
    color: var(--text-secondary);
  }

  .book-view {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100vh;
  }

  .toolbar {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 12px 24px;
    background: var(--bg-secondary);
    border-bottom: 1px solid var(--border);
  }

  .back-link {
    color: var(--accent);
    text-decoration: none;
    font-size: 14px;
  }

  .book-title {
    font-size: 16px;
    font-weight: 600;
    flex: 1;
  }

  .toolbar-actions {
    display: flex;
    align-items: center;
    gap: 8px;
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

  .main-area {
    flex: 1;
    display: flex;
    overflow: hidden;
  }

  .editor-pane {
    flex: 1;
    overflow-y: auto;
  }

  .status-section {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 16px;
    padding: 40px 24px;
  }

  .status-message {
    color: var(--text-secondary);
    font-size: 14px;
  }

  .status-message.converting {
    color: var(--accent);
  }

  .status-message.error {
    color: #f97583;
  }

  .spinner {
    width: 24px;
    height: 24px;
    border: 3px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  .btn-secondary {
    padding: 6px 16px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    cursor: pointer;
    font-size: 13px;
  }

  .btn-secondary:hover {
    opacity: 0.8;
  }

  .side-panel {
    width: 360px;
    min-width: 360px;
    border-left: 1px solid var(--border);
    background: var(--bg-secondary);
    overflow-y: auto;
  }
</style>
