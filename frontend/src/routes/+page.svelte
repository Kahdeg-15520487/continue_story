<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import BookList from '$lib/components/BookList.svelte';
  import Icon from '$lib/components/Icon.svelte';
  import { visualViewport } from '$lib/viewport';
  import { api } from '$lib/api';
  import { toastError } from '$lib/toast.svelte.ts';
  import type { BookSummary } from '$lib/types';

  const ACCEPTED = '.epub,.pdf,.docx,.doc,.txt,.html,.htm,.pptx,.xlsx,.xls,.csv,.ipynb,.md';

  let books: BookSummary[] = $state([]);
  let loading = $state(true);
  let dragging = $state(false);
  let uploading = $state(false);
  let uploadProgress = $state(0);
  let uploadError = $state('');
  let loadError = $state('');
  let showNewBookPrompt = $state(false);
  let newBookTitle = $state('');
  let creatingEmpty = $state(false);

  async function loadBooks() {
    loading = true;
    loadError = '';
    try {
      books = await api.listBooks();
    } catch (err: any) {
      loadError = err.message || 'Failed to load books';
      console.error('Failed to load books:', err);
    } finally {
      loading = false;
    }
  }

  function inferTitle(filename: string): string {
    const name = filename.replace(/\.[^.]+$/, '').replace(/[-_]/g, ' ');
    return name.replace(/\b\w/g, c => c.toUpperCase());
  }

  function isAccepted(file: File): boolean {
    const ext = file.name.lastIndexOf('.') >= 0
      ? file.name.slice(file.name.lastIndexOf('.')).toLowerCase()
      : '';
    return ACCEPTED.split(',').map(e => e.trim()).includes(ext);
  }

  async function handleDrop(e: DragEvent) {
    e.preventDefault();
    e.stopPropagation();
    dragging = false;

    const file = e.dataTransfer?.files[0];
    if (!file) return;

    if (!isAccepted(file)) {
      uploadError = `Unsupported file type. Accepted: ${ACCEPTED}`;
      return;
    }

    await uploadFile(file);
  }

  async function handleFileInput(e: Event) {
    const target = e.target as HTMLInputElement;
    const file = target.files?.[0];
    if (file) await uploadFile(file);
    target.value = '';
  }

  async function uploadFile(file: File) {
    uploading = true;
    uploadProgress = 0;
    uploadError = '';

    try {
      const title = inferTitle(file.name);
      const book = await api.createBook({ title });
      await api.upload(book.slug, file, (pct) => {
        uploadProgress = pct;
      });
      goto(`/books/${book.slug}`);
    } catch (err: any) {
      uploadError = err.message || 'Upload failed';
      toastError(uploadError);
      uploading = false;
      await loadBooks();
    }
  }

  async function createEmptyBook() {
    if (!newBookTitle.trim() || creatingEmpty) return;
    creatingEmpty = true;
    try {
      const book = await api.createBook({ title: newBookTitle.trim() });
      goto(`/books/${book.slug}`);
    } catch (err: any) {
      uploadError = err.message || 'Failed to create book';
      toastError(uploadError);
    } finally {
      creatingEmpty = false;
    }
  }

  onMount(loadBooks);
</script>

<svelte:head>
  <title>Library — Story Engine</title>
</svelte:head>

<svelte:window
  ondragover={(e) => { e.preventDefault(); dragging = true; }}
  ondragleave={(e) => { if (!e.relatedTarget) dragging = false; }}
  ondrop={handleDrop}
/>

<div class="shell" use:visualViewport>
  <header class="topbar">
    <a class="brand" href="/">
      <span class="brand-icon"><Icon name="book" size={21} /></span>
      <span class="brand-text">
        <strong>Story Engine</strong>
        <small>writing studio</small>
      </span>
    </a>

    <nav class="nav">
      <a class="nav-link" href="/knowledge" title="Knowledge base">
        <Icon name="knowledge" size={18} /><span>Knowledge</span>
      </a>
      <a class="nav-link" href="/settings" title="Settings">
        <Icon name="settings" size={18} /><span>Settings</span>
      </a>
      <label class="nav-link primary" title="Import a manuscript">
        <Icon name="plus" size={18} /><span>New book</span>
        <input type="file" accept={ACCEPTED} onchange={handleFileInput} />
      </label>
    </nav>
  </header>

  <main class="main">
    <div class="inner">
      <section class="intro">
        <div>
          <p class="eyebrow">Library</p>
          <h1>Your books</h1>
          <p class="lede">
            {books.length
              ? `${books.length} manuscript${books.length === 1 ? '' : 's'} in your studio`
              : 'Start your first manuscript'}
          </p>
        </div>
        <button class="btn-primary" onclick={() => (showNewBookPrompt = !showNewBookPrompt)}>
          <Icon name="plus" size={17} /> Empty book
        </button>
      </section>

      {#if showNewBookPrompt}
        <form
          class="new-book"
          onsubmit={(e) => { e.preventDefault(); createEmptyBook(); }}
        >
          <input
            type="text"
            placeholder="Give the manuscript a title…"
            bind:value={newBookTitle}
            autofocus
            onkeydown={(e) => { if (e.key === 'Escape') { showNewBookPrompt = false; newBookTitle = ''; } }}
          />
          <button class="btn-primary" type="submit" disabled={creatingEmpty || !newBookTitle.trim()}>
            Create
          </button>
          <button
            class="btn-ghost"
            type="button"
            title="Cancel"
            onclick={() => { showNewBookPrompt = false; newBookTitle = ''; }}
          >
            <Icon name="close" size={16} />
          </button>
        </form>
      {/if}

      {#if uploading}
        <div class="uploading">
          <div class="spinner"></div>
          <div class="uploading-body">
            <strong>Importing manuscript</strong>
            <div class="progress"><i style="width: {uploadProgress}%"></i></div>
          </div>
          <span class="uploading-pct">{uploadProgress}%</span>
        </div>
      {/if}

      {#if uploadError}
        <p class="error-banner">{uploadError}</p>
      {/if}

      {#if loading}
        <div class="grid skeleton-grid">
          {#each Array(6) as _}
            <div class="skeleton"></div>
          {/each}
        </div>
      {:else if loadError}
        <div class="empty-state">
          <p>{loadError}</p>
          <button class="btn-primary" onclick={loadBooks}>Try again</button>
        </div>
      {:else}
        <BookList {books} onImport={uploadFile} />
      {/if}
    </div>
  </main>

  {#if dragging}
    <div class="drop-overlay">
      <div class="drop-card">
        <Icon name="upload" size={40} />
        <h2>Drop to import</h2>
        <p>EPUB · PDF · DOCX · TXT · MD</p>
      </div>
    </div>
  {/if}
</div>

<style>
  .shell {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100vh;
    height: var(--vvh, 100dvh);
  }

  .topbar {
    flex-shrink: 0;
    height: var(--appbar-h);
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 16px;
    padding: 0 26px;
    border-bottom: 1px solid var(--border);
    background: rgba(251, 247, 240, 0.82);
    backdrop-filter: blur(14px);
    z-index: 20;
  }

  .brand {
    display: flex;
    align-items: center;
    gap: 11px;
    text-decoration: none;
    min-width: 0;
  }

  .brand-icon {
    width: 38px;
    height: 38px;
    border-radius: 12px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--accent);
    color: #fff;
    box-shadow: var(--shadow-sm);
    flex-shrink: 0;
  }

  .brand-text strong {
    display: block;
    font-family: var(--font-serif);
    font-size: 17px;
    line-height: 1.1;
  }

  .brand-text small {
    display: block;
    font-size: 9.5px;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    color: var(--text-muted);
    margin-top: 2px;
  }

  .nav {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .nav-link {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    gap: 8px;
    height: 38px;
    padding: 0 15px;
    border-radius: var(--r-full);
    border: 1px solid var(--border);
    background: var(--bg-elevated);
    color: var(--text-secondary);
    text-decoration: none;
    font-size: 13px;
    font-weight: 500;
    transition: border-color 0.15s, color 0.15s, background 0.15s;
    white-space: nowrap;
  }

  .nav-link:hover {
    color: var(--text-primary);
    border-color: var(--border-strong);
  }

  .nav-link.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: #fff;
  }

  .nav-link.primary:hover {
    background: var(--accent-hover);
    border-color: var(--accent-hover);
    color: #fff;
  }

  .nav-link input {
    display: none;
  }

  .main {
    flex: 1;
    overflow-y: auto;
  }

  .inner {
    max-width: var(--page-max);
    margin: 0 auto;
    padding: 44px 26px 96px;
  }

  .intro {
    display: flex;
    align-items: flex-end;
    justify-content: space-between;
    gap: 18px;
    margin-bottom: 30px;
  }

  .eyebrow {
    font-size: 10.5px;
    letter-spacing: 0.16em;
    text-transform: uppercase;
    font-weight: 700;
    color: var(--accent);
    margin-bottom: 8px;
  }

  .intro h1 {
    font-size: clamp(34px, 5vw, 46px);
    line-height: 1.02;
  }

  .lede {
    margin-top: 9px;
    color: var(--text-secondary);
    font-size: 14px;
  }

  .btn-primary {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    height: 42px;
    padding: 0 18px;
    border: none;
    border-radius: var(--r-full);
    background: var(--accent);
    color: #fff;
    font-size: 13.5px;
    font-weight: 600;
    box-shadow: var(--shadow-sm);
    transition: background 0.15s, transform 0.12s;
    white-space: nowrap;
  }

  .btn-primary:hover {
    background: var(--accent-hover);
  }

  .btn-primary:active {
    transform: translateY(1px);
  }

  .btn-primary:disabled {
    opacity: 0.55;
    cursor: not-allowed;
  }

  .btn-ghost {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 40px;
    height: 40px;
    border: 1px solid var(--border);
    border-radius: var(--r-full);
    background: transparent;
    color: var(--text-secondary);
  }

  .btn-ghost:hover {
    color: var(--text-primary);
    border-color: var(--border-strong);
  }

  .new-book {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 14px;
    margin-bottom: 22px;
    border: 1px solid var(--border);
    border-radius: var(--r-lg);
    background: var(--bg-elevated);
    box-shadow: var(--shadow-sm);
  }

  .new-book input {
    flex: 1;
    min-width: 0;
    height: 42px;
    padding: 0 14px;
    border: 1px solid var(--border);
    border-radius: var(--r-sm);
    background: var(--bg-primary);
    font-size: 15px;
  }

  .new-book input:focus {
    outline: none;
    border-color: var(--accent);
    box-shadow: 0 0 0 3px var(--accent-soft);
  }

  .uploading {
    display: flex;
    align-items: center;
    gap: 14px;
    padding: 16px 18px;
    margin-bottom: 20px;
    border: 1px solid var(--border);
    border-radius: var(--r-lg);
    background: var(--bg-elevated);
    box-shadow: var(--shadow-sm);
  }

  .uploading-body {
    flex: 1;
    min-width: 0;
  }

  .uploading-body strong {
    display: block;
    font-size: 13.5px;
    margin-bottom: 8px;
  }

  .uploading-pct {
    font-size: 13px;
    color: var(--text-secondary);
    font-variant-numeric: tabular-nums;
  }

  .progress {
    height: 6px;
    border-radius: var(--r-full);
    background: var(--bg-tertiary);
    overflow: hidden;
  }

  .progress i {
    display: block;
    height: 100%;
    border-radius: inherit;
    background: var(--accent);
    transition: width 0.2s;
  }

  .spinner {
    width: 18px;
    height: 18px;
    border: 2px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
    flex-shrink: 0;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  .error-banner {
    padding: 12px 16px;
    margin-bottom: 18px;
    border-radius: var(--r-md);
    background: var(--error-bg);
    color: var(--error);
    font-size: 13px;
  }

  .empty-state {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 14px;
    padding: 60px 20px;
    color: var(--text-secondary);
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(228px, 1fr));
    gap: 20px;
  }

  .skeleton-grid {
    opacity: 0.7;
  }

  .skeleton {
    height: 250px;
    border-radius: var(--r-lg);
    background: linear-gradient(100deg, var(--bg-secondary) 30%, var(--bg-tertiary) 50%, var(--bg-secondary) 70%);
    background-size: 200% 100%;
    animation: shimmer 1.4s infinite;
  }

  @keyframes shimmer {
    to { background-position: -200% 0; }
  }

  .drop-overlay {
    position: fixed;
    inset: 0;
    z-index: 60;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 24px;
    background: rgba(60, 45, 30, 0.3);
    backdrop-filter: blur(6px);
  }

  .drop-card {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 10px;
    width: min(420px, 100%);
    padding: 48px 28px;
    border: 2px dashed var(--border-strong);
    border-radius: var(--r-xl);
    background: var(--bg-elevated);
    color: var(--text-secondary);
    box-shadow: var(--shadow-lg);
  }

  .drop-card h2 {
    font-size: 24px;
    color: var(--text-primary);
  }

  .drop-card p {
    font-size: 12px;
    letter-spacing: 0.08em;
    text-transform: uppercase;
  }

  @media (max-width: 720px) {
    .topbar {
      height: 58px;
      padding: 0 14px;
    }

    .nav-link {
      width: 42px;
      height: 42px;
      padding: 0;
      border-radius: 50%;
    }

    .nav-link span {
      display: none;
    }

    .brand-text small {
      display: none;
    }

    .inner {
      padding: 26px 16px 96px;
    }

    .intro {
      align-items: flex-start;
      flex-direction: column;
      gap: 14px;
      margin-bottom: 22px;
    }

    .intro h1 {
      font-size: 34px;
    }

    .new-book {
      flex-wrap: wrap;
      padding: 12px;
    }

    .new-book input {
      flex-basis: 100%;
    }
  }
</style>
