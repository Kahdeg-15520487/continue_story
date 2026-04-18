<script lang="ts">
  import { onMount } from 'svelte';
  import BookList from '$lib/components/BookList.svelte';
  import { api } from '$lib/api';
  import type { BookSummary } from '$lib/types';

  let books: BookSummary[] = $state([]);
  let loading = $state(true);
  let showCreateForm = $state(false);
  let newTitle = $state('');
  let newAuthor = $state('');
  let createError = $state('');

  async function loadBooks() {
    try {
      books = await api.listBooks();
    } catch (err) {
      console.error('Failed to load books:', err);
    } finally {
      loading = false;
    }
  }

  async function createBook() {
    if (!newTitle.trim()) return;
    try {
      await api.createBook({
        title: newTitle.trim(),
        author: newAuthor.trim() || undefined,
      });
      newTitle = '';
      newAuthor = '';
      showCreateForm = false;
      await loadBooks();
    } catch (err: any) {
      createError = err.message || 'Failed to create book';
    }
  }

  onMount(loadBooks);
</script>

<div class="library-page">
  <aside class="sidebar">
    <div class="sidebar-header">
      <h1>📚 Library</h1>
      <button class="btn btn-primary" onclick={() => showCreateForm = !showCreateForm}>
        + New Book
      </button>
    </div>

    {#if showCreateForm}
      <form class="create-form" onsubmit={(e) => { e.preventDefault(); createBook(); }}>
        <input placeholder="Title" bind:value={newTitle} required />
        <input placeholder="Author" bind:value={newAuthor} />
        <div class="form-actions">
          <button type="submit" class="btn btn-primary">Create</button>
          <button type="button" class="btn" onclick={() => showCreateForm = false}>Cancel</button>
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
    <div class="empty-state">
      <h2>Welcome to Knowledge Engine</h2>
      {#if books.length === 0}
        <p>No books yet. Click <strong>+ New Book</strong> in the sidebar to create one, then upload a file to get started.</p>
      {:else}
        <p>Select a book from the library to start reading and editing.</p>
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

  .create-form input {
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 14px;
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

  .btn-primary {
    background: #238636;
    border-color: #238636;
    color: white;
  }

  .btn-primary:hover {
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
  }

  .empty-state h2 {
    font-size: 24px;
    margin-bottom: 8px;
  }

  .create-error {
    padding: 8px 16px;
    background: #3d1f1f;
    color: #f97583;
    font-size: 13px;
  }
</style>
