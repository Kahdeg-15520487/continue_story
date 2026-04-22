<script lang="ts">
  import { api } from '$lib/api';

  let {
    bookSlug,
    chapterId,
    visible = $bindable(false),
    position = { top: 0, left: 0 },
    selectedText = '',
    onEditDone,
    onEditError,
  }: {
    bookSlug: string;
    chapterId: string;
    visible: boolean;
    position: { top: number; left: number };
    selectedText: string;
    onEditDone: (scratchPath: string) => void;
    onEditError: (message: string) => void;
  } = $props();

  let instruction = $state('');
  let streaming = $state(false);
  let error = $state('');
  let abortController: AbortController | null = $state(null);
  let inputEl: HTMLInputElement | undefined = $state();
  let menuEl: HTMLDivElement | undefined = $state();
  let dragOffset = $state<{ x: number; y: number } | null>(null);

  // Note: we intentionally do NOT auto-focus the input here.
  // Focusing the input steals focus from the editor's contenteditable,
  // which causes the browser to visually deselect the user's text selection.
  // The user clicks the input when ready.

  function startDrag(e: MouseEvent) {
    if (!menuEl || (e.target as HTMLElement).closest('input, button')) return;
    const rect = menuEl.getBoundingClientRect();
    dragOffset = { x: e.clientX - rect.left, y: e.clientY - rect.top };
    const onMove = (ev: MouseEvent) => {
      if (!dragOffset || !menuEl) return;
      const x = Math.max(0, Math.min(ev.clientX - dragOffset.x, window.innerWidth - menuEl.offsetWidth));
      const y = Math.max(0, Math.min(ev.clientY - dragOffset.y, window.innerHeight - menuEl.offsetHeight));
      menuEl.style.left = x + 'px';
      menuEl.style.top = y + 'px';
    };
    const onUp = () => {
      dragOffset = null;
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
    };
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }

  $effect(() => {
    if (!visible) {
      instruction = '';
      error = '';
      streaming = false;
      abortController = null;
    }
  });

  function submit() {
    const msg = instruction.trim();
    if (!msg || streaming) return;

    error = '';
    streaming = true;
    abortController = api.inlineEdit(
      bookSlug,
      chapterId,
      selectedText,
      msg,
      () => {},
      (scratchPath) => {
        streaming = false;
        abortController = null;
        if (scratchPath) {
          instruction = '';
          onEditDone(scratchPath);
        }
      },
      (err) => {
        streaming = false;
        abortController = null;
        error = err;
        onEditError(err);
      },
      () => {},
    );
  }

  function cancel() {
    abortController?.abort();
    streaming = false;
    abortController = null;
  }

  function dismiss() {
    if (streaming) cancel();
    visible = false;
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      submit();
    } else if (e.key === 'Escape') {
      dismiss();
    }
  }

  function handleOutsideClick(e: MouseEvent) {
    if (menuEl && !menuEl.contains(e.target as Node)) {
      dismiss();
    }
  }

  $effect(() => {
    if (visible) {
      window.addEventListener('mousedown', handleOutsideClick);
      return () => window.removeEventListener('mousedown', handleOutsideClick);
    }
  });

</script>

{#if visible}
  <div
    bind:this={menuEl}
    class="inline-edit-menu"
    style="top: {position.top + 10}px; left: {position.left}px;"
    onmousedown={startDrag}
  >
    <input
      bind:this={inputEl}
      class="edit-input"
      type="text"
      bind:value={instruction}
      placeholder="Describe the edit..."
      disabled={streaming}
      onkeydown={handleKeydown}
    />
    <div class="edit-actions">
      {#if !streaming}
        <button
          class="btn-submit"
          disabled={!instruction.trim()}
          onclick={submit}
        >✨ Ask AI</button>
      {:else}
        <span class="streaming-status">
          <span class="spinner"></span>
          AI is editing...
        </span>
        <button class="btn-cancel" onclick={cancel}>Cancel</button>
      {/if}
    </div>
    {#if error}
      <div class="edit-error">{error}</div>
    {/if}
  </div>
{/if}

<style>
  .inline-edit-menu {
    position: fixed;
    z-index: 1000;
    width: 320px;
    padding: 12px;
    cursor: grab;
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: 8px;
    box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
    transition: opacity 0.15s, transform 0.15s;
  }

  .inline-edit-menu:active {
    cursor: grabbing;
  }

  .inline-edit-menu input,
  .inline-edit-menu button,
  .inline-edit-menu .edit-error {
    cursor: default;
  }

  .edit-input {
    width: 100%;
    padding: 8px 10px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 13px;
    font-family: inherit;
    box-sizing: border-box;
  }

  .edit-input::placeholder {
    color: var(--text-secondary);
  }

  .edit-input:disabled {
    opacity: 0.5;
  }

  .edit-actions {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 8px;
  }

  .btn-submit {
    flex: 1;
    padding: 6px 12px;
    background: var(--accent, #4a9eff);
    border: none;
    border-radius: 6px;
    color: #fff;
    font-size: 13px;
    cursor: pointer;
    transition: opacity 0.15s;
  }

  .btn-submit:disabled {
    opacity: 0.4;
    cursor: not-allowed;
  }

  .streaming-status {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 1;
    font-size: 13px;
    color: var(--text-secondary);
  }

  .spinner {
    display: inline-block;
    width: 14px;
    height: 14px;
    border: 2px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  .btn-cancel {
    padding: 6px 12px;
    background: none;
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-secondary);
    font-size: 12px;
    cursor: pointer;
    transition: color 0.15s, border-color 0.15s;
  }

  .btn-cancel:hover {
    color: #f97583;
    border-color: #f97583;
  }

  .edit-error {
    margin-top: 8px;
    padding: 6px 10px;
    background: #3d1f1f;
    color: #f97583;
    border-radius: 6px;
    font-size: 12px;
  }
</style>
