<script lang="ts">
  import { goto } from '$app/navigation';
  import KnowledgePanel from '$lib/components/KnowledgePanel.svelte';
  import ChatPanel from '$lib/components/ChatPanel.svelte';
  import { visualViewport } from '$lib/viewport';

  let chatWidth = $state(400);
  let mobileTab = $state<'browse' | 'assistant'>('browse');
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

<div class="knowledge-page" use:visualViewport>
  <div class="knowledge-header">
    <button class="back-btn" onclick={() => goto('/')}>← Library</button>
    <h1>🧠 Shared Knowledge Base</h1>
  </div>

  <div class="knowledge-body" class:show-chat={mobileTab === 'assistant'}>
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

  <nav class="mobile-kb-tabs" aria-label="Knowledge sections">
    <button class:active={mobileTab === 'browse'} onclick={() => (mobileTab = 'browse')}>
      <span class="tab-icon">📚</span><span>Browse</span>
    </button>
    <button class:active={mobileTab === 'assistant'} onclick={() => (mobileTab = 'assistant')}>
      <span class="tab-icon">💬</span><span>Assistant</span>
    </button>
  </nav>
</div>

<style>
  .knowledge-page {
    display: flex;
    flex-direction: column;
    height: 100vh;
    width: 100vw;
  }

  .mobile-kb-tabs {
    display: none;
  }

  .knowledge-header {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 12px 20px;
    background: var(--bg-secondary);
    border-bottom: 1px solid var(--border);
  }

  .back-btn {
    padding: 4px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 13px;
  }

  .back-btn:hover { border-color: var(--accent); color: var(--text-primary); }

  .knowledge-header h1 {
    font-size: 18px;
    font-weight: 600;
    color: var(--text-primary);
  }

  .knowledge-body {
    flex: 1;
    display: flex;
    overflow: hidden;
  }

  .kb-viewer-pane {
    flex: 1;
    overflow: hidden;
    border-right: 1px solid var(--border);
  }

  .kb-chat-pane {
    display: flex;
    flex-direction: column;
    background: var(--bg-primary);
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
    background: var(--accent);
  }

  @media (max-width: 860px) {
    .knowledge-page {
      height: 100dvh;
      height: var(--vvh, 100dvh);
      top: var(--vvt, 0px);
    }

    .knowledge-header {
      padding: 8px 12px;
      gap: 10px;
    }

    .knowledge-header h1 {
      font-size: 15px;
    }

    .knowledge-body {
      flex-direction: column;
    }

    .kb-viewer-pane {
      border-right: none;
      border-bottom: none;
      min-height: 0;
    }

    .kb-chat-pane {
      display: none;
    }

    .knowledge-body.show-chat .kb-viewer-pane {
      display: none;
    }

    .knowledge-body.show-chat .kb-chat-pane {
      display: flex;
      flex: 1;
      min-height: 0;
      width: 100% !important;
      min-width: 0 !important;
      max-height: none;
    }

    .mobile-kb-tabs {
      display: flex;
      flex-shrink: 0;
      height: calc(58px + env(safe-area-inset-bottom));
      padding-bottom: env(safe-area-inset-bottom);
      border-top: 1px solid var(--border);
      background: var(--bg-secondary);
    }

    .mobile-kb-tabs button {
      flex: 1;
      display: flex;
      flex-direction: column;
      align-items: center;
      justify-content: center;
      gap: 3px;
      border: none;
      background: none;
      color: var(--text-secondary);
      font-family: inherit;
      font-size: 10px;
      cursor: pointer;
      -webkit-tap-highlight-color: transparent;
    }

    .mobile-kb-tabs button.active {
      color: var(--accent);
    }

    .mobile-kb-tabs .tab-icon {
      font-size: 19px;
      line-height: 1;
    }

    .resize-handle {
      display: none;
    }

    .knowledge-header {
      flex-wrap: wrap;
      gap: 8px;
      padding: 10px 14px;
    }

    .knowledge-header h1 {
      font-size: 16px;
    }
  }
</style>
