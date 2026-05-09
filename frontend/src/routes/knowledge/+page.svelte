<script lang="ts">
  import { onMount } from 'svelte';
  import { goto } from '$app/navigation';
  import KnowledgePanel from '$lib/components/KnowledgePanel.svelte';
  import { api } from '$lib/api';
  import { marked } from 'marked';

  let currentSessionId = $state<string | null>(null);
  let messages = $state<Array<{ role: string; content: string; thinking?: string }>>([]);
  let inputText = $state('');
  let chatError = $state('');
  let streaming = $state(false);
  let abortController: AbortController | null = null;

  async function initSession() {
    try {
      const info = await api.getKnowledgeChatSession();
      currentSessionId = info.sessionId;
    } catch (err: any) {
      chatError = err.message;
    }
  }

  async function sendMessage() {
    if (!inputText.trim() || streaming) return;
    const msg = inputText.trim();
    inputText = '';
    messages.push({ role: 'user', content: msg });

    if (!currentSessionId) {
      try {
        const info = await api.getKnowledgeChatSession();
        currentSessionId = info.sessionId;
      } catch {
        try {
          const info = await api.createNewKnowledgeChatSession();
          currentSessionId = info.sessionId;
        } catch (err: any) {
          chatError = err.message;
          return;
        }
      }
    }

    streaming = true;
    chatError = '';
    let assistantText = '';
    let thinkingText = '';

    abortController = api.knowledgeChat(
      msg,
      currentSessionId!,
      (chunk) => {
        assistantText += chunk;
      },
      () => {
        streaming = false;
        abortController = null;
        if (assistantText) {
          messages.push({ role: 'assistant', content: assistantText, thinking: thinkingText });
        }
      },
      (err) => {
        streaming = false;
        abortController = null;
        chatError = err;
      },
      (text) => {
        thinkingText += text;
      },
    );
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  }

  function abortChat() {
    if (abortController) {
      abortController.abort();
      abortController = null;
      streaming = false;
    }
  }

  async function clearHistory() {
    if (!confirm('Clear all chat history?')) return;
    abortChat();
    messages = [];
    if (currentSessionId) {
      try {
        await fetch(`/api/knowledge/chat/session/${currentSessionId}`, { method: 'DELETE' });
      } catch {}
    }
    currentSessionId = null;
  }

  let bottomAnchor: HTMLDivElement;
  $effect(() => {
    messages;
    setTimeout(() => bottomAnchor?.scrollIntoView({ behavior: 'smooth' }), 50);
  });

  onMount(initSession);
</script>

<svelte:head>
  <title>Knowledge Base</title>
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
      <div class="chat-messages">
        {#each messages as msg}
          <div class="chat-msg {msg.role}">
            {#if msg.role === 'user'}
              <div class="msg-content user">{msg.content}</div>
            {:else}
              {#if msg.thinking}
                <details class="thinking-details">
                  <summary>💭 Thinking ({msg.thinking.length} chars)</summary>
                  <pre class="thinking-text">{msg.thinking}</pre>
                </details>
              {/if}
              <div class="msg-content assistant">{@html marked.parse(msg.content, { async: false })}</div>
            {/if}
          </div>
        {/each}
        {#if streaming}
          <div class="chat-msg assistant">
            <div class="msg-content assistant streaming">●●●</div>
          </div>
        {/if}
        <div bind:this={bottomAnchor}></div>
      </div>

      {#if chatError}
        <div class="chat-error">{chatError}</div>
      {/if}

      <div class="chat-input-area">
        <button class="btn-clear" onclick={clearHistory} title="Clear chat">🗑</button>
        <textarea
          bind:value={inputText}
          onkeydown={handleKeydown}
          placeholder="Ask the research assistant..."
          disabled={streaming}
          rows={1}
        />
        <button class="btn-send" onclick={sendMessage} disabled={streaming || !inputText.trim()}>
          ➤
        </button>
      </div>
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

  .chat-messages {
    flex: 1;
    overflow-y: auto;
    padding: 16px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .chat-msg { max-width: 100%; }
  .chat-msg.user { align-self: flex-end; }
  .chat-msg.assistant { align-self: flex-start; }

  .msg-content {
    padding: 8px 12px;
    border-radius: 8px;
    font-size: 13px;
    line-height: 1.5;
    max-width: 360px;
    word-wrap: break-word;
  }

  .msg-content.user {
    background: var(--accent, #6366f1);
    color: white;
  }

  .msg-content.assistant {
    background: var(--bg-secondary, #161b22);
    color: var(--text-primary, #c9d1d9);
    max-width: 100%;
  }

  .msg-content.assistant :global(p) { margin-bottom: 8px; }
  .msg-content.assistant :global(p:last-child) { margin-bottom: 0; }
  .msg-content.assistant :global(code) { background: var(--bg-tertiary, #21262d); padding: 1px 4px; border-radius: 3px; font-size: 12px; }
  .msg-content.assistant :global(pre) { background: var(--bg-tertiary, #21262d); padding: 8px; border-radius: 4px; overflow-x: auto; }

  .msg-content.streaming { color: var(--text-secondary, #8b949e); animation: pulse 1.5s infinite; }
  @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.4; } }

  .thinking-details {
    margin-bottom: 8px;
    font-size: 12px;
    color: var(--text-secondary, #8b949e);
  }

  .thinking-text {
    font-size: 11px;
    max-height: 200px;
    overflow-y: auto;
    background: var(--bg-tertiary, #21262d);
    padding: 8px;
    border-radius: 4px;
    margin-top: 4px;
    white-space: pre-wrap;
  }

  .chat-error {
    padding: 8px 16px;
    background: #3d1f1f;
    color: #f97583;
    font-size: 12px;
  }

  .chat-input-area {
    display: flex;
    gap: 8px;
    padding: 12px;
    border-top: 1px solid var(--border, #30363d);
    background: var(--bg-secondary, #161b22);
  }

  .chat-input-area textarea {
    flex: 1;
    padding: 8px 12px;
    background: var(--bg-tertiary, #21262d);
    border: 1px solid var(--border, #30363d);
    border-radius: 6px;
    color: var(--text-primary, #c9d1d9);
    font-size: 13px;
    font-family: inherit;
    resize: none;
    outline: none;
  }

  .chat-input-area textarea:focus { border-color: var(--accent, #6366f1); }

  .btn-clear {
    padding: 6px 10px;
    background: none;
    border: 1px solid var(--border, #30363d);
    border-radius: 6px;
    color: var(--text-secondary, #8b949e);
    cursor: pointer;
    font-size: 13px;
  }

  .btn-clear:hover { border-color: #f97583; color: #f97583; }

  .btn-send {
    padding: 6px 14px;
    background: var(--accent, #6366f1);
    border: none;
    border-radius: 6px;
    color: white;
    cursor: pointer;
    font-size: 14px;
  }

  .btn-send:disabled { opacity: 0.5; cursor: not-allowed; }
  .btn-send:not(:disabled):hover { background: #5558e6; }
</style>
