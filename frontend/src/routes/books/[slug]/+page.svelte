<script lang="ts">
  import { page } from '$app/stores';
  import { onMount } from 'svelte';
  import BookEditor from '$lib/components/BookEditor.svelte';
  import UploadZone from '$lib/components/UploadZone.svelte';
  import ChatPanel from '$lib/components/ChatPanel.svelte';
  import LorePanel from '$lib/components/LorePanel.svelte';
  import { api } from '$lib/api';
  import type { BookDetail, ConversionStatus } from '$lib/types';

  let slug = $derived($page.params.slug);
  let book: BookDetail | null = $state(null);
  let content = $state('');
  let loading = $state(true);
  let error = $state('');
  let isEditing = $state(false);
  let saving = $state(false);
  let showChat = $state(false);
  let showLore = $state(false);

  // Resizable panel state — each panel tracks its own width
  let loreWidth = $state(400);
  let chatWidth = $state(400);
  let activeResize: { key: 'lore' | 'chat'; x: number; startWidth: number } | null = null;

  function startResize(key: 'lore' | 'chat') {
    return (e: MouseEvent) => {
      activeResize = {
        key,
        x: e.clientX,
        startWidth: key === 'lore' ? loreWidth : chatWidth,
      };
      document.addEventListener('mousemove', onResize);
      document.addEventListener('mouseup', stopResize);
      document.body.style.cursor = 'col-resize';
      document.body.style.userSelect = 'none';
    };
  }

  function onResize(e: MouseEvent) {
    if (!activeResize) return;
    const dx = activeResize.x - e.clientX;
    const next = Math.max(280, Math.min(800, activeResize.startWidth + dx));
    if (activeResize.key === 'lore') loreWidth = next;
    else chatWidth = next;
  }

  function stopResize() {
    activeResize = null;
    document.removeEventListener('mousemove', onResize);
    document.removeEventListener('mouseup', stopResize);
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
  }

  let conversionStatus: ConversionStatus | null = $state(null);
  let conversionElapsed = $state('');
  let conversionStartTime: number | null = null;
  let conversionPollInterval: ReturnType<typeof setInterval> | null = null;

  function startConversionPolling() {
    if (conversionPollInterval) return; // already polling
    conversionStartTime = Date.now();
    conversionPollInterval = setInterval(async () => {
      if (!slug) { clearInterval(conversionPollInterval!); conversionPollInterval = null; return; }
      try {
        const status = await api.getConversionStatus(slug);
        conversionStatus = status;

        if (conversionStartTime) {
          const secs = Math.round((Date.now() - conversionStartTime) / 1000);
          conversionElapsed = secs < 60 ? `${secs}s` : `${Math.floor(secs / 60)}m ${secs % 60}s`;
        }

        const updated = await api.getBook(slug);
        if (updated) {
          book = updated;
          if (updated.status === 'generating-lore') {
            // Just keep polling, don't re-call loadBook()
            if (!content) {
              try {
                const result = await api.getBookContent(slug);
                content = result.content;
              } catch { /* not ready yet */ }
            }
          } else if (updated.status === 'ready' || updated.status === 'lore-ready') {
            clearInterval(conversionPollInterval!);
            conversionPollInterval = null;
            conversionStatus = null;
            conversionStartTime = null;
            // Refresh content without flashing loading screen
            try {
              book = await api.getBook(slug);
              const result = await api.getBookContent(slug);
              content = result.content;
            } catch { /* ignore */ }
          } else if (updated.status === 'error') {
            clearInterval(conversionPollInterval!);
            conversionPollInterval = null;
          }
        }
      } catch {
        clearInterval(conversionPollInterval!);
        conversionPollInterval = null;
      }
    }, 2000);
  }

  function handleUploaded(result: { sourceFile: string; status: string }) {
    loadBook();
    startConversionPolling();
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
      if (book.status === 'ready' || book.status === 'lore-ready' || book.status === 'generating-lore' || book.status === 'error') {
        try {
          const result = await api.getBookContent(slug);
          content = result.content;
        } catch { /* no content yet */ }
        if (book.status === 'generating-lore') {
          startConversionPolling();
        }
      } else if (book.status === 'converting') {
        startConversionPolling();
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

  // Check for interrupted tasks
  let interruptedTasks: Array<{ id: number; taskType: string; description: string }> = $state([]);

  $effect(() => {
    if (book) {
      api.getAgentTasks(slug).then(tasks => {
        interruptedTasks = tasks.filter(t => t.status === 'interrupted');
      }).catch(() => {});
    }
  });
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

    {#if interruptedTasks.length > 0}
      <div class="task-banner">
        <span>⚠️ Interrupted task{interruptedTasks.length > 1 ? 's' : ''}: {interruptedTasks.map(t => t.description).join(', ')}</span>
      </div>
    {/if}

    <div class="main-area">
      <div class="editor-pane">
        {#if book.status === 'ready' || book.status === 'lore-ready'}
          <BookEditor
            bind:content
            readonly={!isEditing}
            onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
          />
        {:else if book.status === 'generating-lore'}
          {#if content}
            <div class="lore-banner">
              <div class="spinner-small"></div>
              <span>Generating wiki in the background...</span>
            </div>
            <BookEditor
              bind:content
              readonly={!isEditing}
              onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
            />
          {:else}
            <div class="status-section">
              <div class="conversion-panel">
                <div class="conversion-header">
                  <div class="spinner"></div>
                  <h3>Generating wiki</h3>
                </div>
                <p class="status-message">The book has been converted. Analyzing content to extract characters, locations, themes, and plot summary...</p>
              </div>
            </div>
          {/if}
        {:else if book.status === 'pending'}
          <div class="status-section">
            <p class="status-message">Upload a file to convert to markdown.</p>
            <UploadZone {slug} onUploaded={handleUploaded} />
          </div>
        {:else if book.status === 'converting'}
          <div class="status-section">
            <div class="conversion-panel">
              <div class="conversion-header">
                <div class="spinner"></div>
                <h3>Converting to markdown</h3>
              </div>

              {#if book.sourceFile}
                <div class="conversion-detail">
                  <span class="detail-label">File</span>
                  <span class="detail-value">{book.sourceFile}</span>
                </div>
              {/if}

              {#if conversionElapsed}
                <div class="conversion-detail">
                  <span class="detail-label">Elapsed</span>
                  <span class="detail-value">{conversionElapsed}</span>
                </div>
              {/if}

              {#if conversionStatus?.hangfire}
                <div class="conversion-jobs">
                  <div class="job-stat">
                    <span class="job-num">{conversionStatus.hangfire.processing}</span>
                    <span class="job-label">processing</span>
                  </div>
                  <div class="job-stat">
                    <span class="job-num">{conversionStatus.hangfire.enqueued}</span>
                    <span class="job-label">queued</span>
                  </div>
                  <div class="job-stat">
                    <span class="job-num">{conversionStatus.hangfire.succeeded}</span>
                    <span class="job-label">done</span>
                  </div>
                  {#if conversionStatus.hangfire.failed > 0}
                    <div class="job-stat failed">
                      <span class="job-num">{conversionStatus.hangfire.failed}</span>
                      <span class="job-label">failed</span>
                    </div>
                  {/if}
                </div>
                <a href="/hangfire" target="_blank" class="hangfire-link">View in Hangfire Dashboard</a>
              {/if}
            </div>
          </div>
        {:else}
          {#if content}
            <div class="error-banner">
              <span>⚠️ {book.errorMessage || `Error: ${book.status}`}</span>
              <button class="btn btn-retry" onclick={async () => { await api.triggerLoreGeneration(slug); startConversionPolling(); }}>Retry wiki generation</button>
            </div>
            <BookEditor
              bind:content
              readonly={!isEditing}
              onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
            />
          {:else}
            <div class="status-section">
              <p class="status-message error">
                {book.errorMessage || `Unknown status: ${book.status}`}
              </p>
              <UploadZone {slug} onUploaded={handleUploaded} />
            </div>
          {/if}
        {/if}
      </div>

      {#if showLore}
        <div class="resize-handle" role="separator" onmousedown={startResize('lore')}></div>
        <div class="side-panel" style="width: {loreWidth}px; min-width: {loreWidth}px;">
          <LorePanel {slug} />
        </div>
      {/if}

      {#if showChat}
        <div class="resize-handle" role="separator" onmousedown={startResize('chat')}></div>
        <div class="side-panel" style="width: {chatWidth}px; min-width: {chatWidth}px;">
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

  .conversion-panel {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 24px 32px;
    display: flex;
    flex-direction: column;
    gap: 16px;
    min-width: 320px;
  }

  .conversion-header {
    display: flex;
    align-items: center;
    gap: 12px;
  }

  .conversion-header h3 {
    font-size: 16px;
    font-weight: 600;
    color: var(--text-primary);
    margin: 0;
  }

  .conversion-header .spinner {
    width: 18px;
    height: 18px;
    border-width: 2px;
  }

  .conversion-detail {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding: 4px 0;
  }

  .detail-label {
    color: var(--text-secondary);
    font-size: 13px;
  }

  .detail-value {
    color: var(--text-primary);
    font-size: 13px;
    font-weight: 500;
  }

  .conversion-jobs {
    display: flex;
    gap: 16px;
    padding: 12px 0;
    border-top: 1px solid var(--border);
    border-bottom: 1px solid var(--border);
  }

  .job-stat {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 2px;
  }

  .job-stat.failed .job-num {
    color: #f97583;
  }

  .job-num {
    font-size: 20px;
    font-weight: 700;
    color: var(--accent);
  }

  .job-label {
    font-size: 11px;
    color: var(--text-secondary);
    text-transform: uppercase;
    letter-spacing: 0.5px;
  }

  .hangfire-link {
    color: var(--accent);
    font-size: 12px;
    text-decoration: none;
    text-align: center;
  }

  .hangfire-link:hover {
    text-decoration: underline;
  }

  .resize-handle {
    width: 6px;
    cursor: col-resize;
    background: transparent;
    transition: background 0.15s;
    flex-shrink: 0;
    position: relative;
    z-index: 10;
  }

  .resize-handle::after {
    content: '';
    position: absolute;
    top: 0;
    left: -4px;
    right: -4px;
    bottom: 0;
  }

  .resize-handle:hover,
  .resize-handle:active {
    background: var(--accent, #58a6ff);
  }

  .side-panel {
    min-width: 280px;
    max-width: 800px;
    border-left: 1px solid var(--border);
    background: var(--bg-secondary);
    overflow-y: auto;
  }

  .lore-banner {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 16px;
    background: rgba(56, 139, 253, 0.1);
    border-bottom: 1px solid rgba(56, 139, 253, 0.2);
    color: #79c0ff;
    font-size: 12px;
  }

  .spinner-small {
    width: 14px;
    height: 14px;
    border: 2px solid rgba(56, 139, 253, 0.3);
    border-top-color: #58a6ff;
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
    flex-shrink: 0;
  }

  .error-banner {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 8px 16px;
    background: rgba(249, 117, 131, 0.1);
    border-bottom: 1px solid rgba(249, 117, 131, 0.2);
    color: #f97583;
    font-size: 13px;
  }

  .error-banner .btn-retry {
    margin-left: auto;
    padding: 4px 12px;
    background: rgba(249, 117, 131, 0.15);
    border: 1px solid rgba(249, 117, 131, 0.3);
    border-radius: 4px;
    color: #f97583;
    cursor: pointer;
    font-size: 12px;
    white-space: nowrap;
  }

  .error-banner .btn-retry:hover {
    background: rgba(249, 117, 131, 0.25);
  }

  .task-banner {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 16px;
    background: rgba(210, 153, 34, 0.1);
    border-bottom: 1px solid rgba(210, 153, 34, 0.2);
    color: #d29922;
    font-size: 12px;
  }
</style>
