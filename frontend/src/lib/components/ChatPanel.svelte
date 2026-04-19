<script lang="ts">
  import { api } from '$lib/api';
  import { marked } from 'marked';

  let { slug }: { slug: string } = $props();

  let messages: Array<{ role: 'user' | 'assistant'; text: string; thinking?: string }> = $state([]);
  let input = $state('');
  let streaming = $state(false);
  let currentResponse = $state('');
  let thinkingText = $state('');
  let chatError = $state('');
  let chatContainer: HTMLDivElement;

  $effect(() => {
    const _msgs = messages;
    const _resp = currentResponse;
    if (chatContainer) {
      chatContainer.scrollTop = chatContainer.scrollHeight;
    }
  });

  async function send() {
    const msg = input.trim();
    if (!msg || streaming) return;

    chatError = '';
    messages = [...messages, { role: 'user', text: msg }];
    input = '';
    streaming = true;
    currentResponse = '';
    thinkingText = '';

    api.chat(
      slug,
      msg,
      (chunk) => {
        currentResponse += chunk;
      },
      () => {
        if (currentResponse) {
          messages = [...messages, { role: 'assistant', text: currentResponse, thinking: thinkingText || undefined }];
        }
        currentResponse = '';
        thinkingText = '';
        streaming = false;
      },
      (err) => {
        chatError = err;
      },
      (thinking) => {
        thinkingText = '';
      },
      (thinking) => {
        thinkingText += thinking;
      }
    );
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }

  function renderMarkdown(text: string): string {
    return marked.parse(text, { async: false }) as string;
  }
</script>

<div class="chat-panel">
  <h3 class="panel-title">AI Chat</h3>

  <div class="messages" bind:this={chatContainer}>
    {#if messages.length === 0 && !streaming}
      <p class="empty-hint">Ask a question about this book...</p>
    {/if}

    {#each messages as msg}
      <div class="message" class:user={msg.role === 'user'} class:assistant={msg.role === 'assistant'}>
        <div class="message-role">{msg.role === 'user' ? 'You' : 'AI'}</div>
        {#if msg.role === 'assistant' && msg.thinking}
          <details class="thinking-section thinking-done">
            <summary class="thinking-summary">Thought process ({msg.thinking.length} chars)</summary>
            <pre class="thinking-text">{msg.thinking}</pre>
          </details>
        {/if}
        {#if msg.role === 'assistant'}
          <div class="message-text markdown">{@html renderMarkdown(msg.text)}</div>
        {:else}
          <div class="message-text">{msg.text}</div>
        {/if}
      </div>
    {/each}

    {#if streaming && thinkingText}
      <div class="message assistant">
        <div class="message-role">AI</div>
        <details class="thinking-section" open>
          <summary class="thinking-summary">
            <span class="thinking-spinner"></span>
            Thinking...
          </summary>
          <pre class="thinking-text">{thinkingText}</pre>
        </details>
      </div>
    {/if}

    {#if streaming && currentResponse}
      <div class="message assistant">
        <div class="message-role">AI</div>
        <div class="message-text markdown">{@html renderMarkdown(currentResponse)}<span class="cursor">|</span></div>
      </div>
    {/if}

    {#if chatError}
      <div class="chat-error">{chatError}</div>
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

  .thinking-section {
    margin-top: 4px;
  }

  .thinking-summary {
    display: flex;
    align-items: center;
    gap: 8px;
    color: var(--text-secondary);
    font-size: 12px;
    cursor: pointer;
  }

  .thinking-spinner {
    display: inline-block;
    width: 12px;
    height: 12px;
    border: 2px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }

  .thinking-text {
    margin-top: 8px;
    padding: 8px;
    background: rgba(0, 0, 0, 0.2);
    border-radius: 4px;
    font-size: 11px;
    color: var(--text-secondary);
    max-height: 200px;
    overflow-y: auto;
    white-space: pre-wrap;
    word-break: break-word;
  }

  .cursor {
    animation: blink 1s step-end infinite;
  }

  @keyframes blink {
    50% { opacity: 0; }
  }

  .chat-error {
    background: #3d1f1f;
    color: #f97583;
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 12px;
  }

  /* Markdown rendered content */
  .message-text.markdown :global(p) {
    margin: 0 0 8px;
  }

  .message-text.markdown :global(p:last-child) {
    margin-bottom: 0;
  }

  .message-text.markdown :global(h1) {
    font-size: 1.4em;
    font-weight: 700;
    margin: 12px 0 8px;
    border-bottom: 1px solid var(--border);
    padding-bottom: 4px;
  }

  .message-text.markdown :global(h2) {
    font-size: 1.2em;
    font-weight: 600;
    margin: 10px 0 6px;
  }

  .message-text.markdown :global(h3) {
    font-size: 1.1em;
    font-weight: 600;
    margin: 8px 0 4px;
  }

  .message-text.markdown :global(ul), .message-text.markdown :global(ol) {
    margin: 4px 0;
    padding-left: 20px;
  }

  .message-text.markdown :global(li) {
    margin: 2px 0;
  }

  .message-text.markdown :global(code) {
    background: rgba(0, 0, 0, 0.3);
    padding: 1px 5px;
    border-radius: 3px;
    font-size: 0.9em;
    font-family: 'Consolas', 'Monaco', monospace;
  }

  .message-text.markdown :global(pre) {
    background: rgba(0, 0, 0, 0.3);
    padding: 10px 12px;
    border-radius: 6px;
    overflow-x: auto;
    margin: 8px 0;
  }

  .message-text.markdown :global(pre code) {
    background: none;
    padding: 0;
    font-size: 0.85em;
  }

  .message-text.markdown :global(blockquote) {
    border-left: 3px solid var(--accent);
    padding-left: 12px;
    margin: 8px 0;
    color: var(--text-secondary);
  }

  .message-text.markdown :global(strong) {
    font-weight: 600;
  }

  .message-text.markdown :global(a) {
    color: var(--accent);
    text-decoration: underline;
  }

  .message-text.markdown :global(hr) {
    border: none;
    border-top: 1px solid var(--border);
    margin: 12px 0;
  }

  .message-text.markdown :global(table) {
    border-collapse: collapse;
    margin: 8px 0;
    width: 100%;
  }

  .message-text.markdown :global(th), .message-text.markdown :global(td) {
    border: 1px solid var(--border);
    padding: 4px 8px;
    font-size: 0.9em;
  }

  .message-text.markdown :global(th) {
    background: rgba(0, 0, 0, 0.2);
    font-weight: 600;
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
