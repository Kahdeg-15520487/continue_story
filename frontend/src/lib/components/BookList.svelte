<script lang="ts">
  import { browser } from '$app/environment';
  import type { BookSummary } from '$lib/types';
  import { api } from '$lib/api';
  import { toastError } from '$lib/toast.svelte.ts';
  import Icon from '$lib/components/Icon.svelte';

  let {
    books = $bindable([]),
    onImport,
  }: { books: BookSummary[]; onImport?: (file: File) => void | Promise<void> } = $props();

  const ACCEPTED = '.epub,.pdf,.docx,.doc,.txt,.html,.htm,.pptx,.xlsx,.xls,.csv,.ipynb,.md';

  type SortMode = 'modified' | 'abc';
  let sortMode: SortMode = $state('modified');

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

  let sortedBooks = $derived.by(() => {
    const copy = [...books];
    if (sortMode === 'abc') {
      copy.sort((a, b) => a.title.localeCompare(b.title));
    } else {
      copy.sort((a, b) => new Date(b.updatedAt).getTime() - new Date(a.updatedAt).getTime());
    }
    return copy;
  });

  function toggleSort() {
    sortMode = sortMode === 'modified' ? 'abc' : 'modified';
  }

  function coverStyle(slug: string): string {
    let h = 0;
    for (let i = 0; i < slug.length; i++) h = (h * 31 + slug.charCodeAt(i)) % 360;
    return `--c1: hsl(${h} 28% 58%); --c2: hsl(${(h + 30) % 360} 30% 38%);`;
  }

  function initial(title: string): string {
    const t = title.trim();
    return t ? t[0].toUpperCase() : '·';
  }

  function statusLabel(status: string): string {
    switch (status) {
      case 'ready': return 'Ready';
      case 'lore-ready': return 'Ready';
      case 'converting': return 'Converting';
      case 'splitting': return 'Splitting';
      case 'generating-lore': return 'Building wiki';
      case 'error': return 'Error';
      case 'pending': return 'Pending';
      default: return status;
    }
  }

  function statusTone(status: string): string {
    if (status === 'error') return 'error';
    if (status === 'ready' || status === 'lore-ready') return 'ready';
    if (status === 'converting' || status === 'splitting' || status === 'generating-lore') return 'busy';
    return 'neutral';
  }

  function relativeDate(iso: string): string {
    const time = new Date(iso).getTime();
    if (Number.isNaN(time)) return '';
    const diff = Date.now() - time;
    const mins = Math.floor(diff / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return `${mins}m ago`;
    const hours = Math.floor(mins / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    return new Date(iso).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  async function handleImport(e: Event) {
    const target = e.target as HTMLInputElement;
    const file = target.files?.[0];
    if (file && onImport) await onImport(file);
    target.value = '';
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
      toastError('Failed to delete book');
    }
  }
</script>

<div class="book-list">
  <div class="list-head">
    <span>{sortedBooks.length} {sortedBooks.length === 1 ? 'book' : 'books'}</span>
    <button class="sort-btn" onclick={toggleSort} title="Switch sort order">
      <Icon name="sort" size={14} />
      {sortMode === 'modified' ? 'Recent' : 'A–Z'}
    </button>
  </div>

  <div class="grid">
    {#each sortedBooks as book (book.slug)}
      <div class="book-card">
        <a class="card-link" href="/books/{book.slug}" aria-label={book.title}></a>

        <div class="cover" style={coverStyle(book.slug)}>
          <span class="cover-kicker">Story Engine</span>
          <span class="cover-initial">{initial(book.title)}</span>
          <span class="status status-{statusTone(book.status)}">
            <i></i><span class="status-label">{statusLabel(book.status)}</span>
          </span>
        </div>

        <div class="meta">
          <h3 title={book.title}>{book.title}</h3>
          <p>{book.author || 'No author yet'}</p>
          <span class="updated">{relativeDate(book.updatedAt)}</span>
        </div>

        <button
          class="delete"
          onclick={(e) => deleteBook(book.slug, e)}
          title="Delete book"
          aria-label="Delete {book.title}"
        >
          <Icon name="trash" size={15} />
        </button>
      </div>
    {/each}

    <label class="import-card">
      <input type="file" accept={ACCEPTED} onchange={handleImport} />
      <span class="import-icon"><Icon name="plus" size={22} /></span>
      <strong>Import manuscript</strong>
      <small>EPUB · PDF · DOCX · TXT · MD</small>
    </label>
  </div>
</div>

<style>
  .book-list {
    display: flex;
    flex-direction: column;
    gap: 14px;
  }

  .list-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    color: var(--text-secondary);
    font-size: 12px;
    letter-spacing: 0.02em;
  }

  .sort-btn {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    height: 30px;
    padding: 0 11px;
    border: 1px solid var(--border);
    border-radius: var(--r-full);
    background: var(--bg-elevated);
    color: var(--text-secondary);
    font-size: 11.5px;
    font-weight: 500;
  }

  .sort-btn:hover {
    color: var(--text-primary);
    border-color: var(--border-strong);
  }

  .grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(228px, 1fr));
    gap: 20px;
  }

  .book-card {
    position: relative;
    display: flex;
    flex-direction: column;
    border: 1px solid var(--border);
    border-radius: var(--r-lg);
    background: var(--bg-elevated);
    box-shadow: var(--shadow-sm);
    overflow: hidden;
    transition: transform 0.18s ease, box-shadow 0.18s ease, border-color 0.18s;
  }

  .book-card:hover {
    transform: translateY(-3px);
    box-shadow: var(--shadow-md);
    border-color: var(--border-strong);
  }

  .card-link {
    position: absolute;
    inset: 0;
    z-index: 1;
    border-radius: inherit;
  }

  .cover {
    position: relative;
    height: 158px;
    padding: 16px;
    display: flex;
    flex-direction: column;
    justify-content: space-between;
    background: linear-gradient(150deg, var(--c1), var(--c2));
    color: #fff;
    overflow: hidden;
  }

  .cover::before {
    content: '';
    position: absolute;
    inset: 0;
    background:
      radial-gradient(160px 120px at 18% 6%, rgba(255, 255, 255, 0.32), transparent 65%),
      linear-gradient(170deg, transparent 45%, rgba(0, 0, 0, 0.18));
  }

  .cover::after {
    content: '';
    position: absolute;
    top: 0;
    bottom: 0;
    left: 9px;
    width: 1px;
    background: rgba(255, 255, 255, 0.28);
  }

  .cover-kicker {
    position: relative;
    z-index: 1;
    font-size: 8.5px;
    letter-spacing: 0.2em;
    text-transform: uppercase;
    opacity: 0.8;
  }

  .cover-initial {
    position: relative;
    z-index: 1;
    font-family: var(--font-serif);
    font-size: 64px;
    line-height: 0.9;
    color: rgba(255, 255, 255, 0.94);
    text-shadow: 0 4px 18px rgba(0, 0, 0, 0.22);
  }

  .status {
    position: absolute;
    top: 12px;
    right: 12px;
    z-index: 2;
    display: inline-flex;
    align-items: center;
    gap: 5px;
    padding: 4px 9px;
    border-radius: var(--r-full);
    background: rgba(255, 253, 249, 0.92);
    color: var(--text-primary);
    font-size: 9.5px;
    font-weight: 600;
    letter-spacing: 0.02em;
    backdrop-filter: blur(4px);
  }

  .status i {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--text-muted);
    flex-shrink: 0;
  }

  .status-ready i { background: var(--success); }
  .status-error i { background: var(--error); }
  .status-busy i {
    background: var(--warning);
    animation: pulse 1.3s ease-in-out infinite;
  }

  @keyframes pulse {
    50% { opacity: 0.35; }
  }

  .meta {
    padding: 14px 16px 16px;
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
  }

  .meta h3 {
    font-size: 17px;
    line-height: 1.25;
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
  }

  .meta p {
    font-size: 12.5px;
    color: var(--text-secondary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .updated {
    margin-top: 7px;
    font-size: 10.5px;
    letter-spacing: 0.04em;
    color: var(--text-muted);
    text-transform: uppercase;
  }

  .delete {
    position: absolute;
    right: 10px;
    bottom: 12px;
    z-index: 2;
    width: 30px;
    height: 30px;
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--border);
    border-radius: 50%;
    background: var(--bg-elevated);
    color: var(--text-muted);
    opacity: 0;
    transition: opacity 0.15s, color 0.15s, border-color 0.15s;
  }

  .book-card:hover .delete,
  .delete:focus-visible {
    opacity: 1;
  }

  .delete:hover {
    color: var(--error);
    border-color: var(--error);
  }

  .import-card {
    min-height: 238px;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 7px;
    padding: 20px;
    border: 1.5px dashed var(--border-strong);
    border-radius: var(--r-lg);
    background: transparent;
    color: var(--text-secondary);
    text-align: center;
    cursor: pointer;
    transition: border-color 0.15s, color 0.15s, background 0.15s;
  }

  .import-card:hover {
    border-color: var(--accent);
    color: var(--accent);
    background: var(--accent-soft);
  }

  .import-icon {
    width: 44px;
    height: 44px;
    display: flex;
    align-items: center;
    justify-content: center;
    border: 1px solid var(--border);
    border-radius: 50%;
    background: var(--bg-elevated);
    margin-bottom: 2px;
  }

  .import-card strong {
    font-size: 13px;
    font-weight: 600;
  }

  .import-card small {
    font-size: 9.5px;
    letter-spacing: 0.1em;
    text-transform: uppercase;
    opacity: 0.7;
  }

  .import-card input {
    display: none;
  }

  @media (hover: none) {
    .delete {
      opacity: 1;
    }
  }

  @media (max-width: 640px) {
    .grid {
      grid-template-columns: 1fr;
      gap: 12px;
    }

    .book-card {
      flex-direction: row;
      align-items: stretch;
    }

    .cover {
      width: 92px;
      min-width: 92px;
      height: auto;
      min-height: 118px;
      padding: 12px;
    }

    .cover-kicker {
      display: none;
    }

    .cover-initial {
      font-size: 42px;
      margin: auto 0;
    }

    .status {
      top: 8px;
      right: 8px;
      padding: 5px;
    }

    .status-label {
      display: none;
    }

    .meta {
      flex: 1;
      justify-content: center;
      padding: 14px 14px 14px 16px;
    }

    .meta h3 {
      font-size: 16px;
    }

    .delete {
      right: 8px;
      bottom: 8px;
    }

    .import-card {
      min-height: 108px;
      flex-direction: row;
      gap: 12px;
      text-align: left;
      justify-content: flex-start;
      padding: 0 18px;
    }

    .import-icon {
      margin: 0;
      flex-shrink: 0;
    }

    .import-card small {
      display: none;
    }
  }
</style>
