<script lang="ts">
  import { goto } from '$app/navigation';
  import KnowledgePanel from '$lib/components/KnowledgePanel.svelte';
  import ChatPanel from '$lib/components/ChatPanel.svelte';
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

    <div class="kb-chat-pane">
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
    width: 400px;
    min-width: 400px;
    display: flex;
    flex-direction: column;
    background: var(--bg-primary, #0d1117);
  }
</style>
