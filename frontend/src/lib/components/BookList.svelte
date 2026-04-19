<script lang="ts">
  import type { BookSummary } from '$lib/types';
  import { api } from '$lib/api';

  let { books = $bindable([]), }: { books: BookSummary[] } = $props();

  function statusIcon(status: string): string {
    switch (status) {
      case 'ready': return '✅';
      case 'converting': return '⏳';
      case 'error': return '❌';
      default: return '📄';
    }
  }

  async function deleteBook(slug: string, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    if (!confirm('Delete this book?')) return;
    try {
      await api.deleteBook(slug);
      books = books.filter(b => b.slug !== slug);
    } catch (err) {
      console.error('Delete failed:', err);
    }
  }
</script>

<div class="book-list">
  {#if books.length === 0}
    <p class="empty">No books yet. Create one to get started.</p>
  {:else}
    {#each books as book (book.slug)}
      <a href="/books/{book.slug}" class="book-item">
        <span class="status-icon">{statusIcon(book.status)}</span>
        <div class="book-info">
          <span class="book-title">{book.title}</span>
          {#if book.author}
            <span class="book-author">{book.author}</span>
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

  .btn-delete {
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 13px;
    padding: 4px 8px;
    border-radius: 4px;
    opacity: 0;
    transition: opacity 0.15s, color 0.15s;
    flex-shrink: 0;
  }

  .book-item:hover .btn-delete {
    opacity: 1;
  }

  .btn-delete:hover {
    color: #f97583;
    background: rgba(249, 117, 131, 0.1);
  }
</style>
