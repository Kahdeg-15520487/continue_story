<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import BookList from '$lib/components/BookList.svelte';
  import { api } from '$lib/api';
  import type { BookSummary } from '$lib/types';

  const ACCEPTED = '.epub,.pdf,.docx,.doc,.txt,.html,.htm,.pptx,.xlsx,.xls,.csv,.ipynb,.md';

  let books: BookSummary[] = $state([]);
  let loading = $state(true);
  let showCreateForm = $state(false);
  let newTitle = $state('');
  let newAuthor = $state('');
  let createError = $state('');
  let selectedFile: File | null = $state(null);
  let creating = $state(false);
  let uploadProgress = $state(0);

  async function loadBooks() {
    try {
      books = await api.listBooks();
    } catch (err) {
      console.error('Failed to load books:', err);
    } finally {
      loading = false;
    }
  }

  function handleFileChange(e: Event) {
    const target = e.target as HTMLInputElement;
    const file = target.files?.[0];
    if (file) {
      selectedFile = file;
      // Auto-fill title from filename if empty
      if (!newTitle.trim()) {
        const name = file.name.replace(/\.[^.]+$/, '').replace(/[-_]/g, ' ');
        newTitle = name.replace(/\b\w/g, c => c.toUpperCase());
      }
    }
  }

  function handleDrop(e: DragEvent) {
    e.preventDefault();
    const file = e.dataTransfer?.files[0];
    if (file) {
      selectedFile = file;
      if (!newTitle.trim()) {
        const name = file.name.replace(/\.[^.]+$/, '').replace(/[-_]/g, ' ');
        newTitle = name.replace(/\b\w/g, c => c.toUpperCase());
      }
      showCreateForm = true;
    }
  }

  async function createBook() {
    if (!newTitle.trim()) return;
    createError = '';
    creating = true;

    try {
      const book = await api.createBook({
        title: newTitle.trim(),
        author: newAuthor.trim() || undefined,
      });

      // If a file was selected, upload it immediately
      if (selectedFile) {
        uploadProgress = 0;
        try {
          await api.upload(book.slug, selectedFile, (pct) => {
            uploadProgress = pct;
          });
        } catch (err: any) {
          createError = `Book created but upload failed: ${err.message}`;
          creating = false;
          await loadBooks();
          return;
        }
      }

      // Navigate to the book page
      goto(`/books/${book.slug}`);
    } catch (err: any) {
      createError = err.message || 'Failed to create book';
      creating = false;
    }
  }

  function resetForm() {
    newTitle = '';
    newAuthor = '';
    selectedFile = null;
    createError = '';
    showCreateForm = false;
    creating = false;
    uploadProgress = 0;
  }

  onMount(loadBooks);
</script>

<svelte:window ondrop={handleDrop} ondragover={(e) => e.preventDefault()} />

<div class="library-page">
  <aside class="sidebar">
    <div class="sidebar-header">
      <h1>Library</h1>
      <button class="btn btn-primary" onclick={() => { createError = ''; showCreateForm = !showCreateForm; }} disabled={creating}>
        + New Book
      </button>
    </div>

    {#if showCreateForm}
      <form class="create-form" onsubmit={(e) => { e.preventDefault(); createBook(); }}>
        <input placeholder="Title" bind:value={newTitle} required disabled={creating} />
        <input placeholder="Author (optional)" bind:value={newAuthor} disabled={creating} />

        <label class="file-label">
          {#if selectedFile}
            <span class="file-selected">{selectedFile.name} ({(selectedFile.size / 1024 / 1024).toFixed(1)} MB)</span>
            <button type="button" class="file-remove" onclick={() => selectedFile = null} disabled={creating}>x</button>
          {:else}
            <span class="file-hint">Attach file (EPUB, PDF, DOCX...)</span>
          {/if}
          <input type="file" accept={ACCEPTED} onchange={handleFileChange} disabled={creating} />
        </label>

        {#if creating && selectedFile && uploadProgress > 0}
          <div class="progress-bar">
            <div class="progress-fill" style="width: {uploadProgress}%"></div>
          </div>
          <span class="progress-text">Uploading... {uploadProgress}%</span>
        {/if}

        <div class="form-actions">
          <button type="submit" class="btn btn-primary" disabled={creating}>
            {creating ? 'Creating...' : (selectedFile ? 'Create & Upload' : 'Create')}
          </button>
          <button type="button" class="btn" onclick={resetForm} disabled={creating}>Cancel</button>
        </div>
      </form>
    {/if}

    {#if createError}
      <div class="create-error">{createError}</div>
    {/if}

    {#if loading}
      <p class="loading">Loading...</p>
    {:else}
      <BookList {books} />
    {/if}
  </aside>

  <main class="main-content">
    <div class="empty-state"
      ondrop={handleDrop}
      ondragover={(e: DragEvent) => { e.preventDefault(); e.stopPropagation(); }}
    >
      {#if books.length === 0}
        <h2>No books yet</h2>
        <p>Click <strong>+ New Book</strong> to add one, or drag a file onto this window.</p>
      {:else}
        <h2>Knowledge Engine</h2>
        <p>Select a book from the library, or drag a file here to add a new one.</p>
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
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .sidebar-header h1 {
    font-size: 18px;
    font-weight: 600;
    color: var(--text-primary);
  }

  .create-form {
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  .create-form input[type="text"] {
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 14px;
  }

  .file-label {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border: 1px dashed var(--border);
    border-radius: 6px;
    cursor: pointer;
    position: relative;
    min-height: 38px;
  }

  .file-label:hover {
    border-color: var(--accent);
  }

  .file-label input[type="file"] {
    position: absolute;
    inset: 0;
    opacity: 0;
    cursor: pointer;
  }

  .file-selected {
    font-size: 12px;
    color: var(--text-primary);
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .file-hint {
    font-size: 12px;
    color: var(--text-secondary);
  }

  .file-remove {
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 12px;
    padding: 2px 6px;
    z-index: 1;
  }

  .file-remove:hover {
    color: #f97583;
  }

  .progress-bar {
    height: 4px;
    background: var(--bg-tertiary);
    border-radius: 2px;
    overflow: hidden;
  }

  .progress-fill {
    height: 100%;
    background: var(--accent);
    border-radius: 2px;
    transition: width 0.3s ease;
  }

  .progress-text {
    font-size: 11px;
    color: var(--text-secondary);
  }

  .form-actions {
    display: flex;
    gap: 8px;
  }

  .btn {
    padding: 6px 12px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 13px;
  }

  .btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .btn-primary {
    background: #238636;
    border-color: #238636;
    color: white;
  }

  .btn-primary:hover:not(:disabled) {
    background: #2ea043;
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

  .empty-state {
    text-align: center;
    color: var(--text-secondary);
    padding: 60px;
    border: 2px dashed transparent;
    border-radius: 12px;
  }

  .empty-state h2 {
    font-size: 24px;
    margin-bottom: 8px;
    color: var(--text-primary);
  }

  .create-error {
    padding: 8px 16px;
    background: #3d1f1f;
    color: #f97583;
    font-size: 13px;
  }
</style>
