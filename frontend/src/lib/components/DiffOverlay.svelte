<script lang="ts">
  import { diffLines } from 'diff';

  let {
    originalContent,
    scratchContent,
    onAccept,
    onReject,
  }: {
    originalContent: string;
    scratchContent: string;
    onAccept: () => void;
    onReject: () => void;
  } = $props();

  let chunks = $derived(diffLines(originalContent, scratchContent));

  let addedCount = $derived(
    chunks.filter(c => c.added).reduce((sum, c) => sum + c.value.split('\n').filter(l => l).length, 0),
  );

  let removedCount = $derived(
    chunks.filter(c => c.removed).reduce((sum, c) => sum + c.value.split('\n').filter(l => l).length, 0),
  );
</script>

<div class="diff-overlay">
  <div class="diff-header">
    <span class="diff-title">AI Edit Preview</span>
    <div class="diff-stats">
      <span class="stat-added">+{addedCount} lines added</span>
      <span class="stat-removed">-{removedCount} lines removed</span>
    </div>
  </div>
  <div class="diff-content">
    {#each chunks as chunk}
      {#if chunk.added}
        {#each chunk.value.split('\n') as line}
          {#if line}
            <div class="diff-line added"><span class="line-num">+</span>{line}</div>
          {/if}
        {/each}
      {:else if chunk.removed}
        {#each chunk.value.split('\n') as line}
          {#if line}
            <div class="diff-line removed"><span class="line-num">-</span>{line}</div>
          {/if}
        {/each}
      {:else}
        {#each chunk.value.split('\n') as line}
          {#if line}
            <div class="diff-line"><span class="line-num">{'\u00A0'}</span>{line}</div>
          {/if}
        {/each}
      {/if}
    {/each}
  </div>
  <div class="diff-actions">
    <button class="btn-accept" onclick={onAccept}>✓ Accept</button>
    <button class="btn-reject" onclick={onReject}>✗ Reject</button>
  </div>
</div>

<style>
  .diff-overlay {
    display: flex;
    flex-direction: column;
    height: 100%;
    background: var(--bg-primary);
  }

  .diff-header {
    position: sticky;
    top: 0;
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-secondary);
    z-index: 1;
  }

  .diff-title {
    font-size: 14px;
    font-weight: 600;
    color: var(--text-primary);
  }

  .diff-stats {
    display: flex;
    gap: 12px;
    font-size: 12px;
  }

  .stat-added {
    color: #3fb950;
  }

  .stat-removed {
    color: #f85149;
  }

  .diff-content {
    flex: 1;
    overflow-y: auto;
    padding: 0;
    font-family: Consolas, Monaco, 'Courier New', monospace;
    font-size: 14px;
    line-height: 1.6;
  }

  .diff-line {
    padding: 0 16px;
    white-space: pre-wrap;
    word-break: break-word;
    border-left: 3px solid transparent;
  }

  .diff-line.added {
    background: rgba(46, 160, 67, 0.15);
    border-left-color: #2ea043;
  }

  .diff-line.removed {
    background: rgba(248, 81, 73, 0.15);
    border-left-color: #f85149;
  }

  .line-num {
    display: inline-block;
    width: 40px;
    color: var(--text-secondary);
    user-select: none;
  }

  .diff-actions {
    position: sticky;
    bottom: 0;
    display: flex;
    justify-content: center;
    gap: 12px;
    padding: 12px 16px;
    border-top: 1px solid var(--border);
    background: var(--bg-secondary);
  }

  .btn-accept {
    padding: 8px 24px;
    background: #238636;
    border: none;
    border-radius: 6px;
    color: #fff;
    font-size: 14px;
    font-weight: 600;
    cursor: pointer;
    transition: background 0.15s;
  }

  .btn-accept:hover {
    background: #2ea043;
  }

  .btn-reject {
    padding: 8px 24px;
    background: none;
    border: 1px solid #f85149;
    border-radius: 6px;
    color: #f85149;
    font-size: 14px;
    font-weight: 600;
    cursor: pointer;
    transition: background 0.15s, color 0.15s;
  }

  .btn-reject:hover {
    background: rgba(248, 81, 73, 0.15);
    color: #fff;
  }
</style>
