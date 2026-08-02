<script lang="ts">
  import { toasts, dismiss } from '$lib/toast.svelte.ts';

  const icons: Record<string, string> = {
    error: '⚠️',
    success: '✓',
    info: 'ℹ️',
  };
</script>

{#if toasts.length > 0}
  <div class="toast-container" role="status" aria-live="polite">
    {#each toasts as t (t.id)}
      <div class="toast {t.kind}">
        <span class="toast-icon">{icons[t.kind]}</span>
        <span class="toast-message">{t.message}</span>
        <button class="toast-dismiss" onclick={() => dismiss(t.id)} aria-label="Dismiss notification">×</button>
      </div>
    {/each}
  </div>
{/if}

<style>
  .toast-container {
    position: fixed;
    bottom: 16px;
    right: 16px;
    z-index: 1000;
    display: flex;
    flex-direction: column;
    gap: 8px;
    max-width: min(420px, calc(100vw - 32px));
  }

  .toast {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 10px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-left: 3px solid var(--text-secondary);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 13px;
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.4);
    animation: toast-in 0.15s ease-out;
  }

  .toast.error { border-left-color: var(--error); }
  .toast.success { border-left-color: var(--success); }
  .toast.info { border-left-color: var(--accent); }

  .toast-icon { flex: none; }

  .toast-message {
    flex: 1;
    overflow-wrap: anywhere;
  }

  .toast-dismiss {
    flex: none;
    background: none;
    border: none;
    color: var(--text-secondary);
    font-size: 16px;
    cursor: pointer;
    padding: 0 2px;
    line-height: 1;
  }

  .toast-dismiss:hover { color: var(--text-primary); }

  @keyframes toast-in {
    from { opacity: 0; transform: translateY(8px); }
    to { opacity: 1; transform: translateY(0); }
  }
</style>
