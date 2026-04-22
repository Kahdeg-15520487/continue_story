<script lang="ts">
  import { onMount } from 'svelte';
  import { Editor, rootCtx, defaultValueCtx, editorViewCtx } from '@milkdown/kit/core';
  import type { EditorView } from 'prosemirror-view';
  import { commonmark } from '@milkdown/kit/preset/commonmark';
  import { gfm } from '@milkdown/kit/preset/gfm';
  import { nord } from '@milkdown/theme-nord';
  import '@milkdown/theme-nord/style.css';
  import { listener, listenerCtx } from '@milkdown/plugin-listener';

  let { content = $bindable(''), readonly = $bindable(false), onContentChange, onTextSelect }: {
    content: string;
    readonly: boolean;
    onContentChange?: (markdown: string) => void;
    onTextSelect?: (text: string, range: { from: number; to: number }, coords: { top: number; left: number }) => void;
  } = $props();

  let editorEl: HTMLDivElement;
  let editor: Editor | null = $state(null);
  let lastContent = $state(content);

  function setupSelectionListeners(editorView: EditorView) {
    const handleSelectionChange = () => {
      if (!onTextSelect) return;
      const { state } = editorView;
      const { from, to } = state.selection;
      const text = state.doc.textBetween(from, to);
      if (text.trim()) {
        const start = editorView.coordsAtPos(from);
        const end = editorView.coordsAtPos(to);
        onTextSelect(text, { from, to }, {
          top: start.top - 40,
          left: (start.left + end.left) / 2,
        });
      } else {
        onTextSelect('', { from: 0, to: 0 }, { top: 0, left: 0 });
      }
    };

    const handleKeyup = (e: KeyboardEvent) => {
      if (e.shiftKey) handleSelectionChange();
    };

    editorView.dom.addEventListener('mouseup', handleSelectionChange);
    editorView.dom.addEventListener('keyup', handleKeyup);

    return () => {
      editorView.dom.removeEventListener('mouseup', handleSelectionChange);
      editorView.dom.removeEventListener('keyup', handleKeyup);
    };
  }

  let cleanupSelectionListeners: (() => void) | null = null;

  onMount(async () => {
    editor = await Editor.make()
      .config((ctx) => {
        ctx.set(rootCtx, editorEl);
        ctx.set(defaultValueCtx, content);
        ctx.set(listenerCtx, {});
      })
      .use(commonmark)
      .use(gfm)
      .use(nord)
      .use(listener)
      .create();

    const listenerManager = editor.ctx.get(listenerCtx);
    listenerManager.markdownUpdated((_ctx, markdown, _prevMarkdown) => {
      if (markdown !== lastContent) {
        lastContent = markdown;
        content = markdown;
        onContentChange?.(markdown);
      }
    });

    const editorView = editor.ctx.get(editorViewCtx);
    cleanupSelectionListeners = setupSelectionListeners(editorView);
  });

  // React to external content changes (e.g., parent loads new content)
  $effect(() => {
    if (!editor) return;
    // Only replace content if it changed from outside (not from our own edit)
    if (content !== lastContent) {
      lastContent = content;
      cleanupSelectionListeners?.();
      cleanupSelectionListeners = null;

      // Milkdown v7 doesn't have a clean setContent API, so destroy & recreate
      editor.destroy();
      editor = null;
      Editor.make()
        .config((ctx) => {
          ctx.set(rootCtx, editorEl);
          ctx.set(defaultValueCtx, content);
          ctx.set(listenerCtx, {});
        })
        .use(commonmark)
        .use(gfm)
        .use(nord)
        .use(listener)
        .create()
        .then((e) => {
          editor = e;
          const listenerManager = e.ctx.get(listenerCtx);
          listenerManager.markdownUpdated((_ctx, markdown, _prevMarkdown) => {
            if (markdown !== lastContent) {
              lastContent = markdown;
              content = markdown;
              onContentChange?.(markdown);
            }
          });

          const editorView = e.ctx.get(editorViewCtx);
          cleanupSelectionListeners = setupSelectionListeners(editorView);
        });
    }
  });

  $effect(() => {
    if (!editor) return;
    const editorView = editor.ctx.get(editorViewCtx);
    if (readonly) {
      editorView.dom.setAttribute('contenteditable', 'false');
      editorView.dom.style.opacity = '1';
    } else {
      editorView.dom.setAttribute('contenteditable', 'true');
    }
  });
</script>

<div class="milkdown-wrapper" class:readonly>
  <div bind:this={editorEl}></div>
</div>

<style>
  .milkdown-wrapper {
    width: 100%;
    height: 100%;
    overflow-y: auto;
    padding: 32px;
  }

  .milkdown-wrapper :global(.milkdown) {
    max-width: 800px;
    margin: 0 auto;
    color: var(--text-primary);
    font-size: 16px;
    line-height: 1.7;
  }

  .milkdown-wrapper :global(.milkdown h1) {
    font-size: 2em;
    font-weight: 700;
    margin-bottom: 16px;
    color: var(--text-primary);
    border-bottom: 1px solid var(--border);
    padding-bottom: 8px;
  }

  .milkdown-wrapper :global(.milkdown h2) {
    font-size: 1.5em;
    font-weight: 600;
    margin-top: 24px;
    margin-bottom: 12px;
    color: var(--text-primary);
  }

  .milkdown-wrapper :global(.milkdown p) {
    margin-bottom: 16px;
  }

  .milkdown-wrapper :global(.milkdown code) {
    background: var(--bg-tertiary);
    padding: 2px 6px;
    border-radius: 4px;
    font-size: 0.9em;
  }

  .milkdown-wrapper :global(.milkdown pre) {
    background: var(--bg-tertiary);
    padding: 16px;
    border-radius: 8px;
    overflow-x: auto;
    margin-bottom: 16px;
  }

  .milkdown-wrapper :global(.milkdown blockquote) {
    border-left: 3px solid var(--accent);
    padding-left: 16px;
    margin-left: 0;
    color: var(--text-secondary);
  }

  .milkdown-wrapper :global(.milkdown hr) {
    border: none;
    border-top: 1px solid var(--border);
    margin: 24px 0;
  }

  .milkdown-wrapper.readonly :global(.milkdown) {
    pointer-events: none;
  }
</style>
