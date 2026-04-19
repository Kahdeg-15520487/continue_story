<script lang="ts">
  import { api } from '$lib/api';
  import type { ChapterInfo } from '$lib/types';

  let { slug, activeChapterId = $bindable(), onChapterSelect }: {
    slug: string;
    activeChapterId: string | null;
    onChapterSelect?: (id: string) => void;
  } = $props();

  let chapters: ChapterInfo[] = $state([]);
  let loading = $state(true);
  let adding = $state(false);
  let newTitle = $state('');
  let collapsed = $state(false);

  async function loadChapters() {
    try {
      chapters = await api.listChapters(slug);
    } catch {
      chapters = [];
    } finally {
      loading = false;
    }
  }

  async function addChapter() {
    if (!newTitle.trim() || adding) return;
    adding = true;
    try {
      const afterId = chapters.length > 0 ? chapters[chapters.length - 1].id : undefined;
      const created = await api.insertChapter(slug, newTitle.trim(), afterId);
      chapters = [...chapters, created];
      newTitle = '';
      activeChapterId = created.id;
      onChapterSelect?.(created.id);
    } catch (err) {
      console.error('Failed to add chapter:', err);
    } finally {
      adding = false;
    }
  }

  async function removeChapter(id: string) {
    try {
      await api.deleteChapter(slug, id);
      chapters = chapters.filter(c => c.id !== id);
      if (activeChapterId === id) {
        activeChapterId = chapters.length > 0 ? chapters[0].id : null;
        if (activeChapterId) onChapterSelect?.(activeChapterId);
      }
    } catch (err) {
      console.error('Failed to delete chapter:', err);
    }
  }

  async function refresh() {
    await loadChapters();
  }

  $effect(() => {
    loadChapters();
  });

  export { refresh };
</script>

{#if !collapsed}
  <div class="sidebar">
    <div class="sidebar-header">
      <span class="sidebar-title">Chapters</span>
      <span class="chapter-count">{chapters.length}</span>
      <button class="icon-btn" onclick={() => collapsed = true} title="Collapse">◀</button>
    </div>

    {#if loading}
      <div class="sidebar-empty">Loading...</div>
    {:else if chapters.length === 0}
      <div class="sidebar-empty">No chapters yet</div>
    {:else}
      <div class="chapter-list">
        {#each chapters as chapter (chapter.id)}
          <button
            class="chapter-item"
            class:active={activeChapterId === chapter.id}
            onclick={() => { activeChapterId = chapter.id; onChapterSelect?.(chapter.id); }}
          >
            <span class="chapter-number">{chapter.number}</span>
            <span class="chapter-title">{chapter.title}</span>
            <span class="chapter-words">{chapter.wordCount}w</span>
            <button class="delete-btn" onclick|stopPropagation={() => removeChapter(chapter.id)} title="Delete chapter">×</button>
          </button>
        {/each}
      </div>
    {/if}

    <div class="add-chapter">
      <input
        type="text"
        placeholder="New chapter title..."
        bind:value={newTitle}
        onkeydown={(e) => { if (e.key === 'Enter') addChapter(); }}
        disabled={adding}
      />
      <button onclick={addChapter} disabled={adding || !newTitle.trim()}>+</button>
    </div>
  </div>
{:else}
  <button class="collapsed-toggle" onclick={() => collapsed = false} title="Expand chapters">
    ▶
  </button>
{/if}

<style>
  .sidebar {
    display: flex;
    flex-direction: column;
    width: 220px;
    min-width: 220px;
    background: var(--bg-secondary);
    border-right: 1px solid var(--border);
    overflow-y: auto;
    user-select: none;
  }

  .sidebar-header {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 10px 12px;
    border-bottom: 1px solid var(--border);
    font-size: 13px;
    font-weight: 600;
    color: var(--text-secondary);
  }

  .sidebar-title {
    flex: 1;
  }

  .chapter-count {
    font-size: 11px;
    background: var(--bg-tertiary);
    padding: 1px 6px;
    border-radius: 10px;
    color: var(--text-secondary);
  }

  .icon-btn {
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    padding: 2px 4px;
    font-size: 12px;
    border-radius: 3px;
  }

  .icon-btn:hover {
    background: var(--bg-tertiary);
  }

  .sidebar-empty {
    padding: 16px 12px;
    color: var(--text-secondary);
    font-size: 12px;
    text-align: center;
  }

  .chapter-list {
    flex: 1;
    overflow-y: auto;
  }

  .chapter-item {
    display: flex;
    align-items: center;
    gap: 6px;
    width: 100%;
    padding: 8px 12px;
    background: none;
    border: none;
    border-bottom: 1px solid var(--border);
    color: var(--text-primary);
    cursor: pointer;
    text-align: left;
    font-size: 12px;
    transition: background 0.1s;
  }

  .chapter-item:hover {
    background: var(--bg-tertiary);
  }

  .chapter-item.active {
    background: rgba(88, 166, 255, 0.1);
    border-left: 3px solid var(--accent, #58a6ff);
    padding-left: 9px;
  }

  .chapter-number {
    font-size: 11px;
    color: var(--text-secondary);
    min-width: 18px;
    text-align: right;
  }

  .chapter-title {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .chapter-words {
    font-size: 10px;
    color: var(--text-secondary);
    opacity: 0.7;
  }

  .delete-btn {
    display: none;
    background: none;
    border: none;
    color: #f97583;
    cursor: pointer;
    padding: 0 2px;
    font-size: 14px;
    line-height: 1;
  }

  .chapter-item:hover .delete-btn {
    display: inline;
  }

  .add-chapter {
    display: flex;
    gap: 4px;
    padding: 8px;
    border-top: 1px solid var(--border);
  }

  .add-chapter input {
    flex: 1;
    padding: 4px 8px;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    font-size: 12px;
    outline: none;
  }

  .add-chapter input:focus {
    border-color: var(--accent, #58a6ff);
  }

  .add-chapter button {
    padding: 4px 10px;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 14px;
  }

  .add-chapter button:hover:not(:disabled) {
    background: var(--accent, #58a6ff);
    color: #fff;
  }

  .add-chapter button:disabled {
    opacity: 0.4;
    cursor: default;
  }

  .collapsed-toggle {
    width: 24px;
    min-width: 24px;
    background: var(--bg-secondary);
    border-right: 1px solid var(--border);
    border-top: none;
    border-bottom: none;
    border-left: none;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 10px;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .collapsed-toggle:hover {
    background: var(--bg-tertiary);
  }
</style>
