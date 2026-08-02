<script lang="ts">
  import { goto } from '$app/navigation';
  import KnowledgePanel from '$lib/components/KnowledgePanel.svelte';
  import ChatPanel from '$lib/components/ChatPanel.svelte';

  let chatWidth = $state(400);
  let activeResize: { x: number; startWidth: number } | null = null;

  function startResize(e: MouseEvent) {
    activeResize = { x: e.clientX, startWidth: chatWidth };
    document.addEventListener('mousemove', onResize);
    document.addEventListener('mouseup', stopResize);
    document.body.style.cursor = 'col-resize';
    document.body.style.userSelect = 'none';
  }

  function onResize(e: MouseEvent) {
    if (!activeResize) return;
    const dx = activeResize.x - e.clientX;
    chatWidth = Math.max(280, Math.min(800, activeResize.startWidth + dx));
  }

  function stopResize() {
    activeResize = null;
    document.removeEventListener('mousemove', onResize);
    document.removeEventListener('mouseup', stopResize);
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
  }
</script>

<svelte:head>
  <title>Knowledge Base — Story Engine</title>
</svelte:head>

<div class="knowledge-page">
  <div class="knowledge-header">
    <button class="back-btn" onclick={() => goto('/')}>← Library</button>
    <h1>🧠 Shared Knowledge Base</h1>
  </div>

  <div class="knowledge-body">
    <div class="kb-viewer-pane">
      <KnowledgePanel />
    </div>

    <div
      class="resize-handle"
      role="separator"
      aria-orientation="vertical"
      tabindex="0"
      onmousedown={startResize}
      onkeydown={(e) => {
        if (e.key === 'ArrowLeft') { e.preventDefault(); chatWidth = Math.max(280, chatWidth - 20); }
        if (e.key === 'ArrowRight') { e.preventDefault(); chatWidth = Math.min(800, chatWidth + 20); }
      }}
    ></div>
    <div class="kb-chat-pane" style="width: {chatWidth}px; min-width: {chatWidth}px;">
      <ChatPanel mode="knowledge" slug="__shared__" />
    </div>
  </div>
</div>

<style>
  .knowledge-page {
    display: flex;
    flex-direction: column;
    height: 100vh;
    width: 100vw;
  }

  .knowledge-header {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 12px 20px;
    background: var(--bg-secondary, #161b22);
    border-bottom: 1px solid var(--border, #30363d);
  }

  .back-btn {
    padding: 4px 12px;
    background: var(--bg-tertiary, #21262d);
    border: 1px solid var(--border, #30363d);
    border-radius: 6px;
    color: var(--text-secondary, #8b949e);
    cursor: pointer;
    font-size: 13px;
  }

  .back-btn:hover { border-color: var(--accent, #6366f1); color: var(--text-primary, #c9d1d9); }

  .knowledge-header h1 {
    font-size: 18px;
    font-weight: 600;
    color: var(--text-primary, #c9d1d9);
  }

  .knowledge-body {
    flex: 1;
    display: flex;
    overflow: hidden;
  }

  .kb-viewer-pane {
    flex: 1;
    overflow: hidden;
    border-right: 1px solid var(--border, #30363d);
  }

  .kb-chat-pane {
    display: flex;
    flex-direction: column;
    background: var(--bg-primary, #0d1117);
  }

  .resize-handle {
    width: 6px;
    cursor: col-resize;
    background: transparent;
    transition: background 0.15s;
    flex-shrink: 0;
    position: relative;
    z-index: 10;
  }

  .resize-handle::after {
    content: '';
    position: absolute;
    top: 0;
    left: -4px;
    right: -4px;
    bottom: 0;
  }

  .resize-handle:hover,
  .resize-handle:active {
    background: var(--accent, #58a6ff);
  }
</style>
