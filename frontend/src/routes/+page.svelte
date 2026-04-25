<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import BookList from '$lib/components/BookList.svelte';
  import { api } from '$lib/api';
  import type { BookSummary } from '$lib/types';

  const ACCEPTED = '.epub,.pdf,.docx,.doc,.txt,.html,.htm,.pptx,.xlsx,.xls,.csv,.ipynb,.md';

  let books: BookSummary[] = $state([]);
  let loading = $state(true);
  let dragging = $state(false);
  let uploading = $state(false);
  let uploadProgress = $state(0);
  let uploadError = $state('');

  async function loadBooks() {
    try {
      books = await api.listBooks();
    } catch (err) {
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
      uploading = false;
      await loadBooks();
    }
  }

  onMount(loadBooks);
</script>

<svelte:window
  ondragover={(e) => { e.preventDefault(); dragging = true; }}
  ondragleave={() => { dragging = false; }}
/>

<div class="library-page">
  <aside class="sidebar">
    <div class="sidebar-header">
      <h1>Library</h1>
    </div>

    {#if loading}
      <p class="loading">Loading...</p>
    {:else}
      <BookList {books} />
    {/if}
  </aside>

  <main class="main-content">
    <div
      class="drop-zone"
      class:dragging
      class:uploading
      ondrop={handleDrop}
      ondragover={(e: DragEvent) => { e.preventDefault(); e.stopPropagation(); dragging = true; }}
      ondragleave={() => { dragging = false; }}
    >
      {#if uploading}
        <div class="upload-progress">
          <div class="spinner"></div>
          <p class="progress-label">Uploading... {uploadProgress}%</p>
          <div class="progress-bar">
            <div class="progress-fill" style="width: {uploadProgress}%"></div>
          </div>
        </div>
      {:else}
        <div class="drop-content">
          <div class="drop-icon">📚</div>
          <h2>{books.length === 0 ? 'Drop a file to create a book' : 'Drop a file to add a new book'}</h2>
          <p class="drop-formats">EPUB, PDF, DOCX, TXT, HTML, MD, and more</p>
          <label class="browse-btn">
            or browse files
            <input type="file" accept={ACCEPTED} onchange={handleFileInput} />
          </label>
        </div>
      {/if}

      {#if uploadError}
        <div class="upload-error">{uploadError}</div>
      {/if}
    </div>
  </main>
</div>

<style>
  .library-page {
    display: flex;
    width: 100%;
    height: 100vh;
  }

  .sidebar {
    width: 280px;
    min-width: 280px;
    background: var(--bg-secondary);
    border-right: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  .sidebar-header {
    padding: 16px;
    border-bottom: 1px solid var(--border);
  }

  .sidebar-header h1 {
    font-size: 18px;
    font-weight: 600;
    color: var(--text-primary);
  }

  .loading {
    padding: 16px;
    color: var(--text-secondary);
  }

  .main-content {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .drop-zone {
    flex: 1;
    height: 100%;
    display: flex;
    align-items: center;
    justify-content: center;
    border: 3px dashed var(--border);
    border-radius: 16px;
    margin: 32px;
    transition: border-color 0.2s, background-color 0.2s;
    position: relative;
  }

  .drop-zone.dragging {
    border-color: var(--accent);
    background: rgba(99, 102, 241, 0.08);
  }

  .drop-zone.uploading {
    border-color: var(--accent);
  }

  .drop-content {
    text-align: center;
    color: var(--text-secondary);
  }

  .drop-icon {
    font-size: 48px;
    margin-bottom: 16px;
    opacity: 0.6;
  }

  .drop-zone h2 {
    font-size: 20px;
    color: var(--text-primary);
    margin-bottom: 8px;
    font-weight: 500;
  }

  .drop-formats {
    font-size: 13px;
    margin-bottom: 16px;
  }

  .browse-btn {
    display: inline-block;
    padding: 8px 20px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 8px;
    color: var(--text-secondary);
    font-size: 13px;
    cursor: pointer;
    position: relative;
    transition: border-color 0.2s, color 0.2s;
  }

  .browse-btn:hover {
    border-color: var(--accent);
    color: var(--text-primary);
  }

  .browse-btn input[type="file"] {
    position: absolute;
    inset: 0;
    opacity: 0;
    cursor: pointer;
  }

  .upload-progress {
    text-align: center;
    width: 300px;
  }

  .spinner {
    width: 32px;
    height: 32px;
    border: 3px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
    margin: 0 auto 16px;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  .progress-label {
    font-size: 14px;
    color: var(--text-primary);
    margin-bottom: 12px;
  }

  .progress-bar {
    height: 6px;
    background: var(--bg-tertiary);
    border-radius: 3px;
    overflow: hidden;
  }

  .progress-fill {
    height: 100%;
    background: var(--accent);
    border-radius: 3px;
    transition: width 0.3s ease;
  }

  .upload-error {
    position: absolute;
    bottom: 24px;
    left: 50%;
    transform: translateX(-50%);
    padding: 8px 16px;
    background: #3d1f1f;
    color: #f97583;
    border-radius: 8px;
    font-size: 13px;
    white-space: nowrap;
  }
</style>
