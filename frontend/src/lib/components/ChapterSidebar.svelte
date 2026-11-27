<script lang="ts">
  import { api } from '$lib/api';
  import { toastError } from '$lib/toast.svelte.ts';
  import type { ChapterInfo } from '$lib/types';
  import Icon from '$lib/components/Icon.svelte';

  let { slug, activeChapterId = $bindable(), collapsed = $bindable(false), onChapterSelect }: {
    slug: string;
    activeChapterId: string | null;
    collapsed: boolean;
    onChapterSelect?: (id: string) => void;
  } = $props();

  let chapters: ChapterInfo[] = $state([]);
  let loading = $state(true);
  let adding = $state(false);
  let newTitle = $state('');
  let showMenu = $state(false);
  let regenerating = $state(false);
  let generatingWiki = $state(false);
  let filter = $state('');

  let filteredChapters = $derived(
    filter.trim()
      ? chapters.filter(c => c.title.toLowerCase().includes(filter.trim().toLowerCase()))
      : chapters,
  );

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
      onChapterSelect?.(created.id);
    } catch (err) {
      console.error('Failed to add chapter:', err);
      toastError('Failed to add chapter');
    } finally {
      adding = false;
    }
  }

  async function removeChapter(id: string) {
    const chapter = chapters.find(c => c.id === id);
    const title = chapter?.title ?? 'this chapter';
    if (!confirm(`Delete "${title}"? This cannot be undone.`)) return;
    try {
      await api.deleteChapter(slug, id);
      chapters = chapters.filter(c => c.id !== id);
      if (activeChapterId === id) {
        const next = chapters.length > 0 ? chapters[0].id : null;
        if (next) onChapterSelect?.(next);
        else activeChapterId = null;
      }
    } catch (err) {
      console.error('Failed to delete chapter:', err);
      toastError('Failed to delete chapter');
    }
  }

  async function regenerateTitles() {
    if (regenerating) return;
    regenerating = true;
    // Keep the menu open in its busy state; poll until titles change (or timeout)
    const snapshot = chapters.map(c => c.title);
    try {
      await api.regenerateTitles(slug);
      const started = Date.now();
      const poll = async () => {
        await loadChapters();
        const changed = chapters.length !== snapshot.length || chapters.some((c, i) => c.title !== snapshot[i]);
        if (changed) {
          regenerating = false;
          showMenu = false;
          return;
        }
        if (Date.now() - started > 120_000) {
          regenerating = false;
          showMenu = false;
          toastError('Title regeneration timed out');
          return;
        }
        setTimeout(poll, 3000);
      };
      setTimeout(poll, 3000);
    } catch (err) {
      console.error('Failed to regenerate titles:', err);
      toastError('Failed to regenerate titles');
      regenerating = false;
    }
  }

  async function generateWiki() {
    if (generatingWiki) return;
    generatingWiki = true;
    showMenu = false;
    try {
      await api.triggerLoreGeneration(slug);
    } catch (err) {
      console.error('Failed to generate wiki:', err);
      toastError('Failed to generate wiki');
    } finally {
      generatingWiki = false;
    }
  }

  async function refresh() {
    await loadChapters();
  }

  $effect(() => {
    loadChapters();
  });

  function handleClickOutside(e: MouseEvent) {
    if (showMenu) showMenu = false;
  }

  export { refresh };
</script>

{#if !collapsed}
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="sidebar" onclick={handleClickOutside}>
    <div class="sidebar-header">
      <span class="sidebar-title">Chapters</span>
      <span class="chapter-count">{chapters.length}</span>
      <div class="menu-wrapper">
        <button class="icon-btn" onclick={(e) => { e.stopPropagation(); showMenu = !showMenu; }} title="More actions"><Icon name="more" size={16} /></button>
        {#if showMenu}
          <div class="menu-dropdown" class:regenerating>
            <button class="menu-item" onclick={regenerateTitles} disabled={regenerating}>
              {regenerating ? 'Regenerating...' : 'Regenerate Titles'}
            </button>
            <button class="menu-item" onclick={generateWiki} disabled={generatingWiki}>
              {generatingWiki ? 'Generating...' : 'Generate Wiki'}
            </button>
          </div>
        {/if}
      </div>
      <button class="icon-btn" onclick={() => collapsed = true} title="Collapse"><Icon name="back" size={16} /></button>
    </div>

    {#if loading}
      <div class="sidebar-empty">Loading...</div>
    {:else if chapters.length === 0}
      <div class="sidebar-empty">No chapters yet</div>
    {:else}
      {#if chapters.length > 1}
        <div class="chapter-filter">
          <input
            type="text"
            placeholder="Search chapters..."
            bind:value={filter}
            onkeydown={(e) => { if (e.key === 'Escape') filter = ''; }}
          />
        </div>
      {/if}
      <div class="chapter-list">
        {#each filteredChapters as chapter (chapter.id)}
          <button
            class="chapter-item"
            class:active={activeChapterId === chapter.id}
            onclick={() => onChapterSelect?.(chapter.id)}
            title={chapter.title}
          >
            <span class="chapter-number">{chapter.number}</span>
            <span class="chapter-title">{chapter.title}</span>
            <span class="chapter-words">{chapter.wordCount}w</span>
            <span
              class="delete-btn"
              role="button"
              tabindex="0"
              onclick={(e) => { e.stopPropagation(); removeChapter(chapter.id); }}
              onkeydown={(e) => { if (e.key === 'Enter') removeChapter(chapter.id); }}
              title="Delete chapter"
            ><Icon name="trash" size={13} /></span>
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
    <Icon name="chevron" size={16} />
  </button>
{/if}

<style>
  .sidebar {
    display: flex;
    flex-direction: column;
    width: 252px;
    min-width: 252px;
    background: var(--bg-secondary);
    border-right: 1px solid var(--border);
    overflow: hidden;
    user-select: none;
  }

  .sidebar-header {
    display: flex;
    align-items: center;
    gap: 7px;
    padding: 16px 12px 10px;
    font-size: 13px;
    font-weight: 600;
    color: var(--text-secondary);
  }

  .sidebar-title {
    flex: 1;
    font-family: var(--font-serif);
    font-size: 18px;
    font-weight: 600;
    color: var(--text-primary);
  }

  .chapter-count {
    font-size: 10px;
    background: var(--bg-elevated);
    border: 1px solid var(--border);
    padding: 2px 7px;
    border-radius: var(--r-full);
    color: var(--text-secondary);
  }

  .icon-btn {
    width: 28px;
    height: 28px;
    display: flex;
    align-items: center;
    justify-content: center;
    background: none;
    border: none;
    color: var(--text-secondary);
    cursor: pointer;
    border-radius: 7px;
  }

  .icon-btn:hover {
    background: var(--bg-tertiary);
    color: var(--text-primary);
  }

  .menu-wrapper {
    position: relative;
  }

  .menu-dropdown {
    position: absolute;
    top: 100%;
    right: 0;
    margin-top: 6px;
    background: var(--bg-elevated);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: var(--shadow-md);
    z-index: 100;
    min-width: 180px;
    padding: 6px;
  }

  .menu-item {
    display: block;
    width: 100%;
    padding: 8px 10px;
    background: none;
    border: none;
    color: var(--text-primary);
    font-size: 12.5px;
    text-align: left;
    cursor: pointer;
    border-radius: 8px;
  }

  .menu-item:hover:not(:disabled) {
    background: var(--bg-tertiary);
  }

  .menu-item:disabled {
    color: var(--text-secondary);
    cursor: not-allowed;
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
    padding: 0 8px 8px;
  }

  .chapter-filter {
    padding: 0 12px 10px;
  }

  .chapter-filter input {
    width: 100%;
    height: 34px;
    padding: 0 10px;
    background: var(--bg-elevated);
    border: 1px solid var(--border);
    border-radius: 9px;
    color: var(--text-primary);
    font-size: 12.5px;
    outline: none;
  }

  .chapter-filter input:focus {
    border-color: var(--accent);
  }

  .chapter-item {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    padding: 9px 10px;
    background: none;
    border: none;
    border-radius: 9px;
    color: var(--text-primary);
    cursor: pointer;
    text-align: left;
    font-size: 13px;
    transition: background 0.12s, box-shadow 0.12s;
  }

  .chapter-item:hover {
    background: var(--bg-tertiary);
  }

  .chapter-item.active {
    background: var(--bg-elevated);
    box-shadow: inset 2px 0 0 var(--accent);
  }

  .chapter-number {
    width: 22px;
    min-width: 22px;
    height: 22px;
    display: flex;
    align-items: center;
    justify-content: center;
    border-radius: 50%;
    background: var(--bg-tertiary);
    font-size: 10px;
    color: var(--text-secondary);
  }

  .chapter-item.active .chapter-number {
    background: var(--accent);
    color: #fff;
  }

  .chapter-title {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    font-weight: 500;
  }

  .chapter-words {
    font-size: 10px;
    color: var(--text-muted);
  }

  .delete-btn {
    display: flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    background: none;
    border: none;
    color: var(--text-muted);
    cursor: pointer;
    opacity: 0;
    border-radius: 6px;
    transition: opacity 0.15s, color 0.15s;
  }

  .chapter-item:hover .delete-btn,
  .chapter-item:focus-within .delete-btn,
  .delete-btn:focus-visible {
    opacity: 1;
  }

  .add-chapter {
    display: flex;
    gap: 6px;
    padding: 10px 12px calc(10px + env(safe-area-inset-bottom));
    border-top: 1px solid var(--border);
  }

  .add-chapter input {
    flex: 1;
    height: 34px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: 9px;
    background: var(--bg-elevated);
    color: var(--text-primary);
    font-size: 12.5px;
    outline: none;
  }

  .add-chapter input:focus {
    border-color: var(--accent);
  }

  .add-chapter button {
    width: 34px;
    height: 34px;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0;
    border: 1px solid var(--border);
    border-radius: 9px;
    background: var(--bg-elevated);
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 16px;
  }

  .add-chapter button:hover:not(:disabled) {
    background: var(--accent);
    color: #fff;
  }

  .add-chapter button:disabled {
    opacity: 0.4;
    cursor: default;
  }

  @media (hover: none) {
    .delete-btn {
      opacity: 1;
    }
  }

  .collapsed-toggle {
    width: 26px;
    min-width: 26px;
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
