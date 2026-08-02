<script lang="ts">
  import { browser } from '$app/environment';
  import type { BookSummary } from '$lib/types';
  import { api } from '$lib/api';
  import { toastError } from '$lib/toast.svelte.ts';

  let { books = $bindable([]), }: { books: BookSummary[] } = $props();

  type SortMode = 'modified' | 'abc';
  let sortMode: SortMode = $state('modified');

  // Persist the sort preference
  const SORT_KEY = 'book-sort-mode';
  if (browser) {
    try {
      const v = localStorage.getItem(SORT_KEY);
      if (v === 'abc' || v === 'modified') sortMode = v;
    } catch { }
  }

  $effect(() => {
    if (!browser) return;
    try { localStorage.setItem(SORT_KEY, sortMode); } catch { }
  });

  function statusIcon(status: string): string {
    switch (status) {
      case 'ready': return '✅';
      case 'converting': return '⏳';
      case 'error': return '❌';
      default: return '📄';
    }
  }

  function toggleSort() {
    sortMode = sortMode === 'modified' ? 'abc' : 'modified';
  }

  let sortedBooks = $derived.by(() => {
    const copy = [...books];
    if (sortMode === 'abc') {
      copy.sort((a, b) => a.title.localeCompare(b.title));
    } else {
      copy.sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());
    }
    return copy;
  });

  async function deleteBook(slug: string, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    if (!confirm('Delete this book?')) return;
    try {
      await api.deleteBook(slug);
      books = books.filter(b => b.slug !== slug);
    } catch (err) {
      console.error('Delete failed:', err);
      toastError('Failed to delete book');
    }
  }
</script>

<div class="book-list">
  <div class="sort-bar">
    <button class="sort-btn" onclick={toggleSort} title="Switch sort order">
      {#if sortMode === 'modified'}
        <span class="sort-label">🕐 Last Modified</span>
      {:else}
        <span class="sort-label">🔤 A–Z</span>
      {/if}
      <span class="sort-arrow">⇄</span>
    </button>
  </div>

  {#if sortedBooks.length === 0}
    <p class="empty">No books yet. Create one to get started.</p>
  {:else}
    {#each sortedBooks as book (book.slug)}
      <a href="/books/{book.slug}" class="book-item">
        <span class="status-icon" title={book.status}>{statusIcon(book.status)}</span>
        <div class="book-info">
          <span class="book-title" title={book.title}>{book.title}</span>
          {#if book.author}
            <span class="book-author">{book.author}</span>
          {/if}
          {#if book.status === 'error' && book.errorMessage}
            <span class="book-error" title={book.errorMessage}>{book.errorMessage}</span>
          {/if}
        </div>
        <button class="btn-delete" onclick={(e) => deleteBook(book.slug, e)} title="Delete book">✕</button>
      </a>
    {/each}
  {/if}
</div>

<style>
  .book-list {
    flex: 1;
    overflow-y: auto;
    padding: 8px;
  }

  .sort-bar {
    padding: 4px 8px 6px;
    display: flex;
    justify-content: flex-end;
  }

  .sort-btn {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 4px 10px;
    font-size: 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-secondary);
    cursor: pointer;
    transition: border-color 0.2s, color 0.2s;
  }

  .sort-btn:hover {
    border-color: var(--accent);
    color: var(--text-primary);
  }

  .sort-label {
    white-space: nowrap;
  }

  .sort-arrow {
    font-size: 11px;
    opacity: 0.6;
  }

  .empty {
    padding: 16px;
    color: var(--text-secondary);
    font-size: 13px;
  }

  .book-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 10px 12px;
    border-radius: 6px;
    text-decoration: none;
    color: var(--text-primary);
    transition: background 0.15s;
  }

  .book-item:hover {
    background: var(--bg-tertiary);
  }

  .status-icon {
    font-size: 16px;
    flex-shrink: 0;
  }

  .book-info {
    display: flex;
    flex-direction: column;
    min-width: 0;
    flex: 1;
  }

  .book-title {
    font-size: 14px;
    font-weight: 500;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .book-author {
    font-size: 12px;
    color: var(--text-secondary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .book-error {
    font-size: 11px;
    color: var(--error);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .btn-delete {
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 13px;
    padding: 4px 8px;
    border-radius: 4px;
    opacity: 0.35;
    transition: opacity 0.15s, color 0.15s;
    flex-shrink: 0;
  }

  .book-item:hover .btn-delete,
  .book-item:focus-within .btn-delete,
  .btn-delete:focus-visible {
    opacity: 1;
  }

  .btn-delete:hover {
    color: #f97583;
    background: rgba(249, 117, 131, 0.1);
  }
</style>
