<script lang="ts">
  import type { BookSummary } from '$lib/types';

  let { books }: { books: BookSummary[] } = $props();

  function statusIcon(status: string): string {
    switch (status) {
      case 'ready': return '✅';
      case 'converting': return '⏳';
      case 'error': return '❌';
      default: return '📄';
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
</style>
