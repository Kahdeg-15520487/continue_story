<script lang="ts">
  import { page } from '$app/stores';
  import { onMount } from 'svelte';
  import BookEditor from '$lib/components/BookEditor.svelte';
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

  async function triggerConversion() {
    try {
      await api.triggerConversion(slug);
      // Poll for completion
      const interval = setInterval(async () => {
        await loadBook();
        if (book?.status === 'ready' || book?.status === 'error') {
          clearInterval(interval);
          if (book.status === 'ready') {
            const result = await api.getBookContent(slug);
            content = result.content;
          }
        }
      }, 3000);
    } catch (err) {
      console.error('Conversion failed:', err);
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
    <!-- Toolbar -->
    <div class="toolbar">
      <a href="/" class="back-link">← Library</a>
      <h2 class="book-title">{book.title}</h2>
      <div class="toolbar-actions">
        {#if book.status === 'pending'}
          <button class="btn btn-primary" onclick={triggerConversion}>
            Convert
          </button>
        {:else if book.status === 'converting'}
          <span class="status-converting">⏳ Converting...</span>
        {:else if book.status === 'ready'}
          <button class="btn" onclick={() => isEditing = !isEditing}>
            {isEditing ? '🔒 Lock' : '✏️ Edit'}
          </button>
          {#if saving}
            <span class="status-saving">Saving...</span>
          {/if}
        {:else if book.status === 'error'}
          <span class="status-error">❌ Conversion failed</span>
        {/if}
        <button class="btn" onclick={() => showLore = !showLore}>
          📖 Wiki
        </button>
        <button class="btn" onclick={() => showChat = !showChat}>
          💬 Chat
        </button>
      </div>
    </div>

    <!-- Main area -->
    <div class="main-area">
      <div class="editor-pane">
        {#if book.status === 'ready'}
          <BookEditor
            bind:content
            readonly={!isEditing}
            onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
          />
        {:else}
          <div class="empty-editor">
            {#if book.status === 'pending'}
              <p>Book not yet converted. Click "Convert" to process it.</p>
            {:else if book.status === 'converting'}
              <p>Conversion in progress...</p>
            {:else}
              <p>Conversion failed: {book.errorMessage}</p>
            {/if}
          </div>
        {/if}
      </div>

      <!-- Side panels -->
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

  .btn-primary {
    background: #238636;
    border-color: #238636;
    color: white;
  }

  .status-converting, .status-saving {
    color: var(--warning);
    font-size: 13px;
  }

  .status-error {
    color: var(--error);
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

  .empty-editor {
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--text-secondary);
  }

  .side-panel {
    width: 360px;
    min-width: 360px;
    border-left: 1px solid var(--border);
    background: var(--bg-secondary);
    overflow-y: auto;
  }
</style>
