<script lang="ts">
  import { onMount } from 'svelte';
  import Icon from '$lib/components/Icon.svelte';
  import { api } from '$lib/api';
  import { renderMarkdown } from '$lib/markdown';
  import { describeToolActivity } from '$lib/tool-labels';

  let {
    slug,
    activeChapterId = null,
    onEditDone,
    onResponseDone,
    mode = 'book',
    onClose,
  }: {
    slug: string;
    activeChapterId?: string | null;
    onEditDone?: (chapterId: string) => void;
    onResponseDone?: () => void;
    mode?: 'book' | 'knowledge';
    onClose?: () => void;
  } = $props();

  let messages: Array<{ role: 'user' | 'assistant'; text: string; thinking?: string; createdAt?: number }> = $state([]);
  let input = $state('');
  let streaming = $state(false);
  let currentResponse = $state('');
  let thinkingText = $state('');
  let chatError = $state('');
  let chatContainer: HTMLDivElement;
  let currentSessionId = $state<string | null>(null);
  let abortController: AbortController | null = null;
  let activeTools = $state<Array<{ name: string; args: any; id: number }>>([]);
  let completedTools = $state<Array<{ name: string; args: any; result?: string; isError: boolean; id: number }>>([]);
  let toolIdCounter = 0;

  // Debounce the markdown re-render of the streaming response
  let displayResponse = $state('');
  let renderTimer: ReturnType<typeof setTimeout> | null = null;

  $effect(() => {
    const raw = currentResponse;
    if (renderTimer) { clearTimeout(renderTimer); renderTimer = null; }
    if (!raw) { displayResponse = ''; return; }
    renderTimer = setTimeout(() => { displayResponse = raw; }, 120);
  });

  let copiedText = $state<string | null>(null);
  let copyTimer: ReturnType<typeof setTimeout> | null = null;
  let textareaEl: HTMLTextAreaElement | null = null;

  function formatTime(ts?: number): string {
    if (!ts) return '';
    const diff = Date.now() - ts;
    if (diff < 60_000) return 'just now';
    if (diff < 3_600_000) return `${Math.floor(diff / 60_000)}m ago`;
    return new Date(ts).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  }

  async function copyMessage(text: string) {
    try {
      await navigator.clipboard.writeText(text);
      copiedText = text;
      if (copyTimer) clearTimeout(copyTimer);
      copyTimer = setTimeout(() => { copiedText = null; }, 1500);
    } catch {
      // Clipboard unavailable
    }
  }

  function autoResize() {
    const el = textareaEl;
    if (!el) return;
    el.style.height = 'auto';
    el.style.height = Math.min(el.scrollHeight, 320) + 'px';
  }

  $effect(() => {
    const _msgs = messages;
    const _resp = currentResponse;
    const el = chatContainer;
    if (el) {
      // Only auto-scroll when the user is already near the bottom
      const nearBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40;
      if (nearBottom) el.scrollTop = el.scrollHeight;
    }
  });

  onMount(async () => {
    if (mode === 'knowledge') {
      try {
        const info = await api.getKnowledgeChatSession();
        currentSessionId = info.sessionId;
      } catch {
        // Session will be created on first message
      }

      try {
        const history = await api.getChatHistory('__shared__', 100, currentSessionId ?? undefined);
        messages = history.map(m => ({
          role: m.role as 'user' | 'assistant',
          text: m.content,
          createdAt: Date.parse(m.createdAt) || undefined,
        }));
      } catch {
        // No history yet
      }
    } else {
      try {
        const sessionResult = await api.getChatSession(slug);
        currentSessionId = sessionResult.sessionId;
      } catch {
        // Session will be created on first message
      }

      try {
        const history = await api.getChatHistory(slug, 100, currentSessionId ?? undefined);
        messages = history.map(m => ({
          role: m.role as 'user' | 'assistant',
          text: m.content,
          createdAt: Date.parse(m.createdAt) || undefined,
        }));
      } catch {
        // No history yet
      }
    }
  });

  async function send() {
    const msg = input.trim();
    if (!msg || streaming) return;

    chatError = '';
    messages = [...messages, { role: 'user', text: msg, createdAt: Date.now() }];
    input = '';
    if (textareaEl) textareaEl.style.height = 'auto';
    streaming = true;
    currentResponse = '';
    thinkingText = '';
    activeTools = [];
    completedTools = [];

    if (mode === 'knowledge') {
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
            streaming = false;
            return;
          }
        }
      }
      // Save user message to DB (session must exist first so history is scoped)
      api.saveChatMessage('__shared__', 'user', msg, undefined, currentSessionId ?? undefined).catch(() => {});
      abortController = api.knowledgeChat(
        msg,
        currentSessionId!,
        (chunk) => { currentResponse += chunk; },
        () => {
          if (currentResponse) {
            messages = [...messages, { role: 'assistant', text: currentResponse, thinking: thinkingText || undefined, createdAt: Date.now() }];
            api.saveChatMessage('__shared__', 'assistant', currentResponse, thinkingText || undefined, currentSessionId ?? undefined).catch(() => {});
          }
          currentResponse = '';
          thinkingText = '';
          streaming = false;
          abortController = null;
          onResponseDone?.();
        },
        (err) => {
          chatError = err;
          if (currentResponse) {
            const partial = currentResponse + '\n\n_[Stopped due to error]_';
            messages = [...messages, { role: 'assistant', text: partial, thinking: thinkingText || undefined, createdAt: Date.now() }];
            api.saveChatMessage('__shared__', 'assistant', partial, thinkingText || undefined, currentSessionId ?? undefined).catch(() => {});
          }
          currentResponse = '';
          thinkingText = '';
          streaming = false;
          abortController = null;
        },
        (thinking) => { thinkingText += thinking; },
        (toolName, args) => {
          const id = ++toolIdCounter;
          activeTools.push({ name: toolName, args, id });
          completedTools = completedTools.filter(t => true);
        },
        (toolName, result, isError) => {
          const tool = activeTools.find(t => t.name === toolName);
          if (tool) {
            activeTools = activeTools.filter(t => t !== tool);
            let resultStr: string;
            if (typeof result === 'string') resultStr = result.slice(0, 200);
            else resultStr = JSON.stringify(result).slice(0, 200);
            completedTools.push({ ...tool, result: resultStr, isError });
          }
        },
      );
    } else {
      api.chat(
        slug,
        msg,
        (chunk) => { currentResponse += chunk; },
        () => {
          if (currentResponse) {
            messages = [...messages, { role: 'assistant', text: currentResponse, thinking: thinkingText || undefined, createdAt: Date.now() }];
          }
          currentResponse = '';
          thinkingText = '';
          streaming = false;
          onResponseDone?.();
        },
        (err) => {
          chatError = err;
          if (currentResponse) {
            messages = [...messages, { role: 'assistant', text: currentResponse + '\n\n_[Stopped due to error]_', thinking: thinkingText || undefined, createdAt: Date.now() }];
          }
          currentResponse = '';
          thinkingText = '';
          streaming = false;
        },
        (thinking) => { thinkingText += thinking; },
        (toolName, args) => {
          const id = ++toolIdCounter;
          activeTools.push({ name: toolName, args, id });
          completedTools = completedTools.filter(t => true); // trigger reactivity
        },
        (toolName, result, isError) => {
          const tool = activeTools.find(t => t.name === toolName);
          if (tool) {
            activeTools = activeTools.filter(t => t !== tool);
            let resultStr: string;
            if (typeof result === 'string') resultStr = result.slice(0, 200);
            else resultStr = JSON.stringify(result).slice(0, 200);
            completedTools.push({ ...tool, result: resultStr, isError });
          }
        },
        { activeChapterId, sessionId: currentSessionId, onEditDone, onSessionInfo: (id) => { currentSessionId = id; } }
      );
    }
  }

  async function stop() {
    if (!streaming) return;
    try {
      if (mode === 'knowledge') {
        // Abort the agent session server-side, not just the fetch
        if (currentSessionId) {
          await api.abortKnowledgeChat(currentSessionId).catch(() => {});
        }
        abortController?.abort();
        abortController = null;
      } else {
        await api.abortChat(slug);
      }
      if (currentResponse) {
        const stopped = currentResponse + '\n\n_[Stopped]_';
        messages = [...messages, { role: 'assistant', text: stopped, thinking: thinkingText || undefined, createdAt: Date.now() }];
        if (mode === 'knowledge') {
          api.saveChatMessage('__shared__', 'assistant', stopped, thinkingText || undefined, currentSessionId ?? undefined).catch(() => {});
        }
      }
      currentResponse = '';
      thinkingText = '';
      streaming = false;
      currentSessionId = null;
    } catch (err: any) {
      chatError = err.message || 'Failed to stop';
      streaming = false;
    }
  }

  async function retry() {
    if (streaming || mode === 'knowledge') return;
    chatError = '';
    try {
      const { lastUserMessage: msg } = await api.getLastUserMessage(slug);

      if (!msg) { chatError = 'No previous message to retry'; return; }
      if (messages.length > 0 && messages[messages.length - 1].role === 'assistant') {
        messages = messages.slice(0, -1);
      }
      const last = messages[messages.length - 1];
      if (last && last.role === 'user' && last.text === msg) {
        // Replace the duplicate user bubble instead of appending another
        messages = messages.slice(0, -1);
      }
      messages = [...messages, { role: 'user', text: msg, createdAt: Date.now() }];
      streaming = true;
      currentResponse = '';
      thinkingText = '';
      activeTools = [];
      completedTools = [];
      currentSessionId = null;
      api.chat(
        slug,
        msg,
        (chunk) => { currentResponse += chunk; },
        () => {
          if (currentResponse) {
            messages = [...messages, { role: 'assistant', text: currentResponse, thinking: thinkingText || undefined, createdAt: Date.now() }];
          }
          currentResponse = '';
          thinkingText = '';
          streaming = false;
          onResponseDone?.();
        },
        (err) => {
          chatError = err;
          if (currentResponse) {
            messages = [...messages, { role: 'assistant', text: currentResponse + '\n\n_[Stopped due to error]_', thinking: thinkingText || undefined, createdAt: Date.now() }];
          }
          currentResponse = '';
          thinkingText = '';
          streaming = false;
        },
        (thinking) => { thinkingText += thinking; },
        (toolName, args) => {
          const id = ++toolIdCounter;
          activeTools.push({ name: toolName, args, id });
          completedTools = completedTools.filter(t => true); // trigger reactivity
        },
        (toolName, result, isError) => {
          const tool = activeTools.find(t => t.name === toolName);
          if (tool) {
            activeTools = activeTools.filter(t => t !== tool);
            let resultStr: string;
            if (typeof result === 'string') resultStr = result.slice(0, 200);
            else resultStr = JSON.stringify(result).slice(0, 200);
            completedTools.push({ ...tool, result: resultStr, isError });
          }
        },
        { activeChapterId, sessionId: null, onEditDone, onSessionInfo: (id) => { currentSessionId = id; } }
      );
    } catch (err: any) {
      chatError = err.message || 'Failed to retry';
    }
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send(); }
  }

  async function startNewSession() {
    try {
      if (mode === 'knowledge') {
        const info = await api.createNewKnowledgeChatSession();
        currentSessionId = info.sessionId;
      } else {
        const { sessionId } = await api.createNewChatSession(slug);
        currentSessionId = sessionId;
      }
      messages = [];
      chatError = '';
    } catch (err: any) {
      chatError = err.message || 'Failed to create new session';
    }
  }

  async function clearHistory() {
    if (!confirm('Clear all chat history?')) return;
    try {
      if (mode === 'knowledge') {
        if (currentSessionId) {
          await fetch(`/api/knowledge/chat/session/${currentSessionId}`, { method: 'DELETE' });
        }
        await api.clearChatHistory('__shared__');
      } else {
        await api.clearChatHistory(slug, currentSessionId ?? undefined);
      }
      messages = [];
      currentSessionId = null;
      chatError = '';
    } catch (err: any) { chatError = err.message; }
  }
</script>

<div class="chat-panel">
  <div class="panel-header">
    <div class="panel-heading">
      <span class="assistant-badge"><Icon name="sparkles" size={15} /></span>
      <div class="panel-title-group">
        <h3 class="panel-title">{mode === 'knowledge' ? 'Research assistant' : 'Assistant'}</h3>
        <p class="panel-sub">{mode === 'knowledge' ? 'Shared knowledge base' : 'Ask about this book'}</p>
      </div>
    </div>
    <div class="session-controls">
      <button class="icon-btn" title="New session" onclick={startNewSession}><Icon name="plus" size={16} /></button>
      {#if messages.length > 0}
        <button class="icon-btn" onclick={clearHistory} title="Clear chat history"><Icon name="trash" size={15} /></button>
      {/if}
      {#if onClose}
        <button class="icon-btn" title="Close chat" aria-label="Close chat" onclick={onClose}><Icon name="close" size={16} /></button>
      {/if}
    </div>
  </div>

  <div class="messages" bind:this={chatContainer} aria-live="polite">
    {#if messages.length === 0 && !streaming}
      <p class="empty-hint">{mode === 'knowledge' ? 'Ask the research assistant...' : 'Ask a question about this book...'}</p>
    {/if}

    {#each messages as msg}
      <div class="message" class:user={msg.role === 'user'} class:assistant={msg.role === 'assistant'}>
        <div class="message-role">
          {msg.role === 'user' ? 'You' : 'AI'}
          {#if msg.createdAt}
            <span class="message-time">{formatTime(msg.createdAt)}</span>
          {/if}
        </div>
        {#if msg.role === 'assistant' && msg.thinking}
          <details class="thinking-section thinking-done">
            <summary class="thinking-summary">Thought process ({msg.thinking.length} chars)</summary>
            <pre class="thinking-text">{msg.thinking}</pre>
          </details>
        {/if}
        {#if msg.role === 'assistant'}
          <div class="message-text markdown">{@html renderMarkdown(msg.text)}</div>
          <button class="copy-btn" onclick={() => copyMessage(msg.text)} title="Copy message">
            {copiedText === msg.text ? 'Copied' : 'Copy'}
          </button>
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

    {#if streaming && (completedTools.length > 0 || activeTools.length > 0)}
      <div class="message assistant">
        <div class="message-role">AI</div>
        {#each completedTools as tool (tool.id)}
          {@const activity = describeToolActivity(tool.name, tool.args)}
          <div class="tool-block completed" class:error={tool.isError}>
            <div class="tool-header">
              <span class="tool-icon">{activity.icon}</span>
              <span class="tool-label">{activity.label}</span>
              <span class="tool-status">✓</span>
            </div>
            {#if tool.result}
              <details>
                <summary>Result</summary>
                <pre class="tool-result">{tool.result}</pre>
              </details>
            {/if}
          </div>
        {/each}
        {#each activeTools as tool (tool.id)}
          {@const activity = describeToolActivity(tool.name, tool.args)}
          <div class="tool-block active">
            <div class="tool-header">
              <span class="tool-icon spinning">{activity.icon}</span>
              <span class="tool-label">{activity.label}</span>
              <span class="tool-status"><span class="spinner"></span></span>
            </div>
          </div>
        {/each}
      </div>
    {/if}

    {#if streaming && displayResponse}
      <div class="message assistant">
        <div class="message-role">AI</div>
        <div class="message-text markdown">{@html renderMarkdown(displayResponse)}<span class="cursor">|</span></div>
      </div>
    {/if}

    {#if chatError}
      <div class="chat-error">{chatError}</div>
    {/if}
  </div>

  <form class="input-form" onsubmit={(e) => { e.preventDefault(); send(); }}>
    <textarea
      bind:this={textareaEl}
      bind:value={input}
      placeholder={mode === 'knowledge' ? 'Ask the research assistant...' : 'Ask about the book...'}
      disabled={streaming}
      onkeydown={handleKeydown}
      oninput={autoResize}
      rows="4"
    ></textarea>
    <div class="input-actions">
      <button type="submit" class="btn btn-send" disabled={streaming || !input.trim()}>
        <Icon name="send" size={16} /> Send
      </button>
      {#if streaming}
        <button type="button" class="btn btn-stop" onclick={stop} title="Stops the response and resets the conversation context">
          <Icon name="close" size={14} /> Stop
        </button>
      {:else if mode === 'book' && messages.length > 0}
        <button type="button" class="btn btn-retry" onclick={retry}>
          <Icon name="refresh" size={14} /> Retry
        </button>
      {/if}
    </div>
  </form>
</div>

<style>
  .chat-panel { display: flex; flex-direction: column; height: 100%; min-height: 0; }
  .panel-header { display: flex; align-items: center; justify-content: space-between; gap: 10px; padding: 13px 16px; border-bottom: 1px solid var(--border); background: var(--bg-elevated); }
  .panel-heading { display: flex; align-items: center; gap: 10px; min-width: 0; }
  .assistant-badge { width: 32px; height: 32px; border-radius: 10px; background: var(--accent-soft); color: var(--accent); display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
  .panel-title { font-size: 15px; line-height: 1.15; }
  .panel-sub { font-size: 10.5px; color: var(--text-muted); margin-top: 1px; }
  .session-controls { display: flex; gap: 4px; align-items: center; }
  .icon-btn { width: 30px; height: 30px; display: flex; align-items: center; justify-content: center; border: none; background: transparent; color: var(--text-secondary); border-radius: 8px; }
  .icon-btn:hover { background: var(--bg-secondary); color: var(--text-primary); }
  .messages { flex: 1; min-height: 0; overflow-y: auto; padding: 18px 16px 8px; display: flex; flex-direction: column; gap: 14px; }
  .empty-hint { color: var(--text-muted); font-size: 13px; text-align: center; padding-top: 28px; }
  .message { font-size: 14px; line-height: 1.65; }
  .message.user { align-self: flex-end; max-width: 88%; background: var(--accent-soft); border-radius: 16px 16px 4px 16px; padding: 10px 14px; }
  .message.assistant { align-self: stretch; max-width: 100%; background: transparent; padding: 0; }
  .message-role { font-size: 10px; font-weight: 700; letter-spacing: 0.08em; color: var(--text-muted); margin-bottom: 5px; text-transform: uppercase; }
  .message.user .message-role { color: var(--accent); }
  .message-time { font-weight: 400; opacity: 0.7; margin-left: 6px; font-size: 9.5px; letter-spacing: 0; text-transform: none; }
  .copy-btn { margin-top: 6px; background: none; border: none; color: var(--text-muted); cursor: pointer; font-size: 10.5px; padding: 2px 0; }
  .copy-btn:hover { color: var(--accent); }
  .message-text { white-space: pre-wrap; word-break: break-word; }
  .thinking-section { margin-top: 4px; }
  .thinking-summary { display: flex; align-items: center; gap: 8px; color: var(--text-secondary); font-size: 12px; cursor: pointer; }
  .thinking-spinner { display: inline-block; width: 12px; height: 12px; border: 2px solid var(--border); border-top-color: var(--accent); border-radius: 50%; animation: spin 0.8s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  .thinking-text { margin-top: 8px; padding: 8px; background: rgba(60, 45, 30, 0.08); border-radius: 4px; font-size: 11px; color: var(--text-secondary); max-height: 200px; overflow-y: auto; white-space: pre-wrap; word-break: break-word; }
  .tool-block { margin: 6px 0; padding: 6px 10px; background: var(--bg-tertiary); border: 1px solid var(--border); border-radius: 6px; font-size: 12px; color: var(--text-secondary); }
  .tool-block.active { border-color: var(--accent); }
  .tool-block.error { border-color: var(--error); }
  .tool-header { display: flex; align-items: center; gap: 6px; }
  .tool-label { flex: 1; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .tool-status { font-size: 11px; }
  .tool-result { margin-top: 6px; padding: 6px; background: var(--bg-primary); border-radius: 4px; max-height: 120px; overflow-y: auto; white-space: pre-wrap; font-size: 11px; }
  .spinner { display: inline-block; width: 10px; height: 10px; border: 2px solid var(--border); border-top-color: var(--accent); border-radius: 50%; animation: spin 0.8s linear infinite; }
  .cursor { animation: blink 1s step-end infinite; }
  @keyframes blink { 50% { opacity: 0; } }
  .chat-error { background: var(--error-bg); color: var(--error); padding: 8px 12px; border-radius: 6px; font-size: 12px; }
  .message-text.markdown :global(p) { margin: 0 0 8px; }
  .message-text.markdown :global(p:last-child) { margin-bottom: 0; }
  .message-text.markdown :global(h1) { font-size: 1.4em; font-weight: 700; margin: 12px 0 8px; border-bottom: 1px solid var(--border); padding-bottom: 4px; }
  .message-text.markdown :global(h2) { font-size: 1.2em; font-weight: 600; margin: 10px 0 6px; }
  .message-text.markdown :global(h3) { font-size: 1.1em; font-weight: 600; margin: 8px 0 4px; }
  .message-text.markdown :global(ul), .message-text.markdown :global(ol) { margin: 4px 0; padding-left: 20px; }
  .message-text.markdown :global(li) { margin: 2px 0; }
  .message-text.markdown :global(code) { background: rgba(60, 45, 30, 0.12); padding: 1px 5px; border-radius: 3px; font-size: 0.9em; font-family: Consolas, Monaco, monospace; }
  .message-text.markdown :global(pre) { background: rgba(60, 45, 30, 0.12); padding: 10px 12px; border-radius: 6px; overflow-x: auto; margin: 8px 0; }
  .message-text.markdown :global(pre code) { background: none; padding: 0; font-size: 0.85em; }
  .message-text.markdown :global(blockquote) { border-left: 3px solid var(--accent); padding-left: 12px; margin: 8px 0; color: var(--text-secondary); }
  .message-text.markdown :global(strong) { font-weight: 600; }
  .message-text.markdown :global(a) { color: var(--accent); text-decoration: underline; }
  .message-text.markdown :global(hr) { border: none; border-top: 1px solid var(--border); margin: 12px 0; }
  .input-form { flex-shrink: 0; display: flex; align-items: flex-end; gap: 10px; padding: 14px 16px calc(14px + env(safe-area-inset-bottom)); border-top: 1px solid var(--border); background: var(--bg-elevated); }
  .input-form textarea { flex: 1; min-height: 104px; padding: 12px 14px; background: var(--bg-primary); border: 1px solid var(--border); border-radius: 14px; color: var(--text-primary); font-size: 15px; line-height: 1.55; resize: none; font-family: inherit; }
  .input-form textarea:focus { outline: none; border-color: var(--accent); box-shadow: 0 0 0 3px var(--accent-soft); }
  .btn { display: inline-flex; align-items: center; justify-content: center; gap: 6px; min-height: 44px; padding: 0 16px; border: 1px solid var(--border); border-radius: 12px; background: var(--bg-elevated); color: var(--text-primary); cursor: pointer; font-size: 13.5px; font-weight: 500; }
  .btn-send { background: var(--accent); border-color: var(--accent); color: #fff; }
  .btn-send:hover:not(:disabled) { background: var(--accent-hover); }
  .btn-stop { border-color: var(--error); color: var(--error); }
  .btn-stop:hover { background: var(--error-bg); }
  .btn-retry { color: var(--text-secondary); }
  .btn-retry:hover { color: var(--accent); border-color: var(--accent); }
  .input-actions { display: flex; flex-shrink: 0; gap: 6px; }
  .btn:disabled { opacity: 0.45; cursor: not-allowed; }

  @media (max-width: 600px) {
    .input-form {
      flex-direction: column;
      align-items: stretch;
      padding: 12px 12px calc(12px + env(safe-area-inset-bottom));
    }

    .input-form textarea {
      min-height: 128px;
      font-size: 16px;
    }

    .input-actions {
      justify-content: flex-end;
    }

    .input-actions .btn {
      flex: 1;
    }
  }
</style>
