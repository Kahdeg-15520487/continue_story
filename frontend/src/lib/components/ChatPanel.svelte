<script lang="ts">
  import { api } from '$lib/api';

  let { slug }: { slug: string } = $props();

  let messages: Array<{ role: 'user' | 'assistant'; text: string }> = $state([]);
  let input = $state('');
  let streaming = $state(false);
  let currentResponse = $state('');
  let chatContainer: HTMLDivElement;

  async function send() {
    const msg = input.trim();
    if (!msg || streaming) return;

    messages = [...messages, { role: 'user', text: msg }];
    input = '';
    streaming = true;
    currentResponse = '';

    api.chat(
      slug,
      msg,
      (chunk) => {
        currentResponse += chunk;
      },
      () => {
        if (currentResponse) {
          messages = [...messages, { role: 'assistant', text: currentResponse }];
        }
        currentResponse = '';
        streaming = false;
      }
    );

    // Scroll to bottom
    $effect(() => {
      if (chatContainer) {
        chatContainer.scrollTop = chatContainer.scrollHeight;
      }
    });
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }
</script>

<div class="chat-panel">
  <h3 class="panel-title">💬 AI Chat</h3>

  <div class="messages" bind:this={chatContainer}>
    {#if messages.length === 0 && !streaming}
      <p class="empty-hint">Ask a question about this book...</p>
    {/if}

    {#each messages as msg}
      <div class="message" class:user={msg.role === 'user'} class:assistant={msg.role === 'assistant'}>
        <div class="message-role">{msg.role === 'user' ? 'You' : 'AI'}</div>
        <div class="message-text">{msg.text}</div>
      </div>
    {/each}

    {#if streaming && currentResponse}
      <div class="message assistant">
        <div class="message-role">AI</div>
        <div class="message-text">{currentResponse}<span class="cursor">▌</span></div>
      </div>
    {/if}
  </div>

  <form class="input-form" onsubmit={(e) => { e.preventDefault(); send(); }}>
    <textarea
      bind:value={input}
      placeholder="Ask about the book..."
      disabled={streaming}
      onkeydown={handleKeydown}
      rows="2"
    ></textarea>
    <button type="submit" class="btn" disabled={streaming || !input.trim()}>
      Send
    </button>
  </form>
</div>

<style>
  .chat-panel {
    display: flex;
    flex-direction: column;
    height: 100%;
  }

  .panel-title {
    padding: 12px 16px;
    font-size: 14px;
    font-weight: 600;
    border-bottom: 1px solid var(--border);
  }

  .messages {
    flex: 1;
    overflow-y: auto;
    padding: 12px 16px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .empty-hint {
    color: var(--text-secondary);
    font-size: 13px;
    text-align: center;
    padding-top: 24px;
  }

  .message {
    padding: 8px 12px;
    border-radius: 8px;
    font-size: 13px;
    line-height: 1.5;
  }

  .message.user {
    background: var(--bg-tertiary);
    margin-left: 32px;
  }

  .message.assistant {
    background: #1a2332;
    margin-right: 32px;
  }

  .message-role {
    font-size: 11px;
    font-weight: 600;
    color: var(--text-secondary);
    margin-bottom: 4px;
    text-transform: uppercase;
  }

  .message-text {
    white-space: pre-wrap;
    word-break: break-word;
  }

  .cursor {
    animation: blink 1s step-end infinite;
  }

  @keyframes blink {
    50% { opacity: 0; }
  }

  .input-form {
    display: flex;
    gap: 8px;
    padding: 12px 16px;
    border-top: 1px solid var(--border);
  }

  .input-form textarea {
    flex: 1;
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 13px;
    resize: none;
    font-family: inherit;
  }

  .btn {
    padding: 6px 14px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 13px;
  }

  .btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
