<script lang="ts">
  import { page } from '$app/stores';
  import { onMount } from 'svelte';
  import BookEditor from '$lib/components/BookEditor.svelte';
  import UploadZone from '$lib/components/UploadZone.svelte';
  import ChatPanel from '$lib/components/ChatPanel.svelte';
  import LorePanel from '$lib/components/LorePanel.svelte';
  import ChapterSidebar from '$lib/components/ChapterSidebar.svelte';
  import InlineEditMenu from '$lib/components/InlineEditMenu.svelte';
  import DiffOverlay from '$lib/components/DiffOverlay.svelte';
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

  let selectedText = $state('');
  let selectionCoords = $state({ top: 0, left: 0 });
  let showInlineEdit = $state(false);
  let isAiEditing = $state(false);
  let diffState = $state<{ original: string; scratch: string } | null>(null);

  let activeChapterId: string | null = $state(null);
  let chapterSidebar: ChapterSidebar | null = $state(null);

  // Reading position persistence
  const STORAGE_KEY = `reading-pos-${slug}`;

  function saveReadingPosition() {
    if (!activeChapterId) return;
    const wrapper = document.querySelector('.editor-pane .milkdown-wrapper') as HTMLDivElement | null;
    const scrollTop = wrapper?.scrollTop ?? 0;
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ chapterId: activeChapterId, scrollTop }));
    } catch { }
  }

  function restoreReadingPosition(): { chapterId: string; scrollTop: number } | null {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      return JSON.parse(raw);
    } catch { return null; }
  }

  // Debounced scroll save
  let scrollSaveTimeout: ReturnType<typeof setTimeout> | null = null;
  function onEditorScroll() {
    if (scrollSaveTimeout) clearTimeout(scrollSaveTimeout);
    scrollSaveTimeout = setTimeout(saveReadingPosition, 500);
  }

  // Each resizable panel tracks its own width
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
    if (conversionPollInterval) return;
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
          if (updated.status === 'splitting' || updated.status === 'generating-lore') {
            // Keep polling, load content if we don't have it yet
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
            // Refresh without flashing
            try {
              book = await api.getBook(slug);
              await loadChapterContent();
            } catch { /* ignore */ }
          } else if (updated.status === 'error') {
            clearInterval(conversionPollInterval!);
            conversionPollInterval = null;
            await loadChapterContent();
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

  async function loadChapterContent() {
    if (!slug) return;

    const saved = restoreReadingPosition();

    // Try saved chapter first, then activeChapterId, then first chapter
    const tryChapterId = saved?.chapterId ?? activeChapterId;

    if (tryChapterId) {
      try {
        const chapter = await api.getChapter(slug, tryChapterId);
        if (chapter) {
          activeChapterId = tryChapterId;
          content = chapter.content;

          // Check for pending scratch file (agent may have edited while page was closed)
          try {
            const scratch = await api.getScratchContent(slug, tryChapterId);
            if (scratch?.content) {
              diffState = { original: chapter.content, scratch: scratch.content };
              showInlineEdit = true;
            }
          } catch { /* no scratch file */ }

          // Restore scroll after render
          if (saved?.scrollTop) {
            setTimeout(() => {
              const wrapper = document.querySelector('.editor-pane .milkdown-wrapper') as HTMLDivElement | null;
              if (wrapper) wrapper.scrollTop = saved.scrollTop;
            }, 100);
          }
          return;
        }
      } catch { /* chapter not found */ }
    }

    try {
      const chapters = await api.listChapters(slug);
      if (chapters.length > 0) {
        activeChapterId = chapters[0].id;
        const chapter = await api.getChapter(slug, activeChapterId);
        if (chapter) {
          content = chapter.content;
          return;
        }
      }
    } catch { /* no chapters yet */ }

    try {
      const result = await api.getBookContent(slug);
      content = result.content;
    } catch { /* no content at all */ }
  }

  async function loadBook() {
    loading = true;
    error = '';
    try {
      book = await api.getBook(slug);
      if (book.status === 'ready' || book.status === 'lore-ready' || book.status === 'splitting' || book.status === 'generating-lore' || book.status === 'error') {
        await loadChapterContent();
        if (book.status === 'splitting' || book.status === 'generating-lore') {
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
    if (saving || !isEditing || !slug) return;
    saving = true;
    try {
      if (activeChapterId) {
        await api.saveChapter(slug, activeChapterId, newContent);
      } else {
        await api.saveBookContent(slug, newContent);
      }
      content = newContent;
      chapterSidebar?.refresh();
    } catch (err) {
      console.error('Save failed:', err);
    } finally {
      saving = false;
    }
  }

  async function handleChapterSelect(id: string) {
    // Save scroll position of current chapter before switching
    saveReadingPosition();
    // Cancel any pending save so it doesn't overwrite the target chapter
    if (saveTimeout) { clearTimeout(saveTimeout); saveTimeout = null; }
    // Dismiss diff UI but keep scratch file on disk (user must explicitly reject)
    diffState = null;
    showInlineEdit = false;
    if (!slug) return;
    try {
      const chapter = await api.getChapter(slug, id);
      if (chapter) {
        content = chapter.content;
        activeChapterId = id;

        // Check for pending scratch file
        try {
          const scratch = await api.getScratchContent(slug, id);
          if (scratch?.content) {
            diffState = { original: chapter.content, scratch: scratch.content };
          }
        } catch { /* no scratch file */ }
      }
    } catch { /* ignore */ }
  }

  function handleTextSelect(text: string, _range: { from: number; to: number }, coords: { top: number; left: number }) {
    selectedText = text;
    selectionCoords = coords;
    showInlineEdit = !!text.trim();
  }

  async function handleEditDone(scratchPath: string) {
    isAiEditing = false;
    showInlineEdit = false;
    try {
      const result = await api.getScratchContent(slug, activeChapterId!);
      diffState = { original: content, scratch: result.content };
    } catch (err) {
      console.error('Failed to load scratch content:', err);
    }
  }

  function handleEditError(message: string) {
    isAiEditing = false;
    // Error is shown inside InlineEditMenu component itself
    console.error('Inline edit error:', message);
  }

  async function handleAcceptEdit() {
    if (!activeChapterId || !diffState) return;
    try {
      await api.acceptInlineEdit(slug, activeChapterId);
      diffState = null;
      await handleChapterSelect(activeChapterId);
      chapterSidebar?.refresh();
    } catch (err) {
      console.error('Accept edit failed:', err);
    }
  }

  async function handleRejectEdit() {
    if (!activeChapterId) return;
    try {
      await api.rejectInlineEdit(slug, activeChapterId);
    } catch {
      // Ignore — scratch might not exist
    }
    diffState = null;
  }

  async function handleChatEditDone(chapterId: string) {
    // If a diff is already showing, reject it first
    if (diffState && activeChapterId) {
      try { await api.rejectInlineEdit(slug, activeChapterId); } catch { /* ignore */ }
      diffState = null;
    }

    if (chapterId !== activeChapterId) {
      await handleChapterSelect(chapterId);
    }

    try {
      const result = await api.getScratchContent(slug, chapterId);
      const chapter = await api.getChapter(slug, chapterId);
      if (chapter) {
        diffState = {
          original: chapter.content,
          scratch: result.content,
        };
        showInlineEdit = true;
      }
    } catch (err: any) {
      console.error('Failed to load chat edit diff:', err);
    }
  }

  async function handleRetry() {
    const msg = book.errorMessage ?? '';
    if (msg.includes('splitting') || msg.includes('Splitting')) {
      await api.triggerChapterSplit(slug);
    } else {
      await api.triggerLoreGeneration(slug);
    }
    startConversionPolling();
  }

  onMount(loadBook);

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

    {#if showInlineEdit && isEditing}
      <InlineEditMenu
        bookSlug={slug}
        chapterId={activeChapterId ?? ''}
        bind:visible={showInlineEdit}
        position={selectionCoords}
        {selectedText}
        onEditDone={handleEditDone}
        onEditError={handleEditError}
      />
    {/if}

    <div class="main-area">
      {#if book.status === 'ready' || book.status === 'lore-ready' || book.status === 'splitting' || book.status === 'generating-lore'}
        <ChapterSidebar bind:this={chapterSidebar} {slug} bind:activeChapterId onChapterSelect={handleChapterSelect} />

        <div class="editor-pane">
          {#if book.status === 'splitting'}
            <div class="lore-banner">
              <div class="spinner-small"></div>
              <span>Splitting into chapters...</span>
            </div>
          {:else if book.status === 'generating-lore'}
            <div class="lore-banner">
              <div class="spinner-small"></div>
              <span>Generating wiki in the background...</span>
            </div>
          {/if}

          {#if diffState}
            <DiffOverlay
              originalContent={diffState.original}
              scratchContent={diffState.scratch}
              onAccept={handleAcceptEdit}
              onReject={handleRejectEdit}
            />
          {:else if content}
            {#key activeChapterId}
              <BookEditor
                {content}
                readonly={!isEditing}
                onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
                onTextSelect={handleTextSelect}
                onScroll={onEditorScroll}
              />
            {/key}
          {:else}
            <div class="status-section">
              <p class="status-message">No content available yet.</p>
            </div>
          {/if}
        </div>
      {:else if book.status === 'pending'}
        <div class="editor-pane">
          <div class="status-section">
            <p class="status-message">Upload a file to convert to markdown.</p>
            <UploadZone {slug} onUploaded={handleUploaded} />
          </div>
        </div>
      {:else if book.status === 'converting'}
        <div class="editor-pane">
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
        </div>
      {:else if book.status === 'error'}
        <div class="editor-pane">
          {#if content}
            <div class="error-banner">
              <span>⚠️ {book.errorMessage || `Error: ${book.status}`}</span>
              <button class="btn btn-retry" onclick={handleRetry}>Retry</button>
            </div>
            <ChapterSidebar bind:this={chapterSidebar} {slug} bind:activeChapterId onChapterSelect={handleChapterSelect} />
            {#if diffState}
              <DiffOverlay
                originalContent={diffState.original}
                scratchContent={diffState.scratch}
                onAccept={handleAcceptEdit}
                onReject={handleRejectEdit}
              />
            {:else}
              {#key activeChapterId}
                <BookEditor
                  {content}
                  readonly={!isEditing}
                  onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
                  onTextSelect={handleTextSelect}
                  onScroll={onEditorScroll}
                />
              {/key}
            {/if}
          {:else}
            <div class="status-section">
              <p class="status-message error">
                {book.errorMessage || `Unknown status: ${book.status}`}
              </p>
              <UploadZone {slug} onUploaded={handleUploaded} />
            </div>
          {/if}
        </div>
      {/if}

      {#if showLore}
        <div class="resize-handle" role="separator" onmousedown={startResize('lore')}></div>
        <div class="side-panel" style="width: {loreWidth}px; min-width: {loreWidth}px;">
          <LorePanel {slug} />
        </div>
      {/if}

      {#if showChat}
        <div class="resize-handle" role="separator" onmousedown={startResize('chat')}></div>
        <div class="side-panel" style="width: {chatWidth}px; min-width: {chatWidth}px;">
          <ChatPanel {slug} {activeChapterId} onEditDone={handleChatEditDone} />
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
    display: flex;
    flex-direction: column;
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
    flex-shrink: 0;
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
