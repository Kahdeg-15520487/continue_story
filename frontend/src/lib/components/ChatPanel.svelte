<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import { renderMarkdown } from '$lib/markdown';
  import { describeToolActivity } from '$lib/tool-labels';

  let {
    slug,
    activeChapterId = null,
    onEditDone,
    onResponseDone,
    mode = 'book',
  }: {
    slug: string;
    activeChapterId?: string | null;
    onEditDone?: (chapterId: string) => void;
    onResponseDone?: () => void;
    mode?: 'book' | 'knowledge';
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
    el.style.height = Math.min(el.scrollHeight, 160) + 'px';
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
    <h3 class="panel-title">AI Chat</h3>
    <div class="session-controls">
      <button class="btn-new-session" title="New session" onclick={startNewSession}>+</button>
      {#if messages.length > 0}
        <button class="btn-clear-history" onclick={clearHistory} title="Clear chat history">Clear</button>
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
            {copiedText === msg.text ? '✓ Copied' : '📋'}
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
      rows="2"
    ></textarea>
    <div class="input-actions">
      <button type="submit" class="btn" disabled={streaming || !input.trim()}>
        Send
      </button>
      {#if streaming}
        <button type="button" class="btn btn-stop" onclick={stop} title="Stops the response and resets the conversation context">⏹ Stop</button>
      {:else if mode === 'book' && messages.length > 0}
        <button type="button" class="btn btn-retry" onclick={retry}>↻ Retry</button>
      {/if}
    </div>
  </form>
</div>

<style>
  .chat-panel { display: flex; flex-direction: column; height: 100%; }
  .panel-header { display: flex; align-items: center; justify-content: space-between; padding: 12px 16px; border-bottom: 1px solid var(--border); }
  .panel-title { font-size: 14px; font-weight: 600; }
  .session-controls { display: flex; gap: 6px; align-items: center; }
  .btn-new-session { width: 26px; height: 26px; border-radius: 6px; border: 1px solid var(--border); background: var(--bg-tertiary); color: var(--text-secondary); font-size: 16px; font-weight: 600; cursor: pointer; display: flex; align-items: center; justify-content: center; transition: background 0.15s; line-height: 1; }
  .btn-new-session:hover { background: var(--bg-hover); color: var(--text-primary); }
  .btn-clear-history { background: none; border: 1px solid var(--border); color: var(--text-secondary); font-size: 11px; padding: 2px 8px; border-radius: 4px; cursor: pointer; }
  .btn-clear-history:hover { color: #f97583; border-color: #f97583; }
  .messages { flex: 1; overflow-y: auto; padding: 12px 16px; display: flex; flex-direction: column; gap: 12px; }
  .empty-hint { color: var(--text-secondary); font-size: 13px; text-align: center; padding-top: 24px; }
  .message { padding: 8px 12px; border-radius: 8px; font-size: 13px; line-height: 1.5; }
  .message.user { background: var(--bg-tertiary); margin-left: 32px; }
  .message.assistant { background: #1a2332; margin-right: 32px; }
  .message-role { font-size: 11px; font-weight: 600; color: var(--text-secondary); margin-bottom: 4px; text-transform: uppercase; }
  .message-time { font-weight: 400; opacity: 0.6; margin-left: 6px; font-size: 10px; text-transform: none; }
  .copy-btn { margin-top: 4px; background: none; border: none; color: var(--text-secondary); cursor: pointer; font-size: 11px; padding: 2px 4px; border-radius: 4px; }
  .copy-btn:hover { color: var(--text-primary); background: var(--bg-tertiary); }
  .message-text { white-space: pre-wrap; word-break: break-word; }
  .thinking-section { margin-top: 4px; }
  .thinking-summary { display: flex; align-items: center; gap: 8px; color: var(--text-secondary); font-size: 12px; cursor: pointer; }
  .thinking-spinner { display: inline-block; width: 12px; height: 12px; border: 2px solid var(--border); border-top-color: var(--accent); border-radius: 50%; animation: spin 0.8s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  .thinking-text { margin-top: 8px; padding: 8px; background: rgba(0,0,0,0.2); border-radius: 4px; font-size: 11px; color: var(--text-secondary); max-height: 200px; overflow-y: auto; white-space: pre-wrap; word-break: break-word; }
  .tool-block { margin: 6px 0; padding: 6px 10px; background: var(--bg-tertiary, #21262d); border: 1px solid var(--border, #30363d); border-radius: 6px; font-size: 12px; color: var(--text-secondary, #8b949e); }
  .tool-block.active { border-color: var(--accent, #6366f1); }
  .tool-block.error { border-color: #f97583; }
  .tool-header { display: flex; align-items: center; gap: 6px; }
  .tool-label { flex: 1; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .tool-status { font-size: 11px; }
  .tool-result { margin-top: 6px; padding: 6px; background: var(--bg-primary, #0d1117); border-radius: 4px; max-height: 120px; overflow-y: auto; white-space: pre-wrap; font-size: 11px; }
  .spinner { display: inline-block; width: 10px; height: 10px; border: 2px solid var(--border, #30363d); border-top-color: var(--accent, #6366f1); border-radius: 50%; animation: spin 0.8s linear infinite; }
  .cursor { animation: blink 1s step-end infinite; }
  @keyframes blink { 50% { opacity: 0; } }
  .chat-error { background: #3d1f1f; color: #f97583; padding: 8px 12px; border-radius: 6px; font-size: 12px; }
  .message-text.markdown :global(p) { margin: 0 0 8px; }
  .message-text.markdown :global(p:last-child) { margin-bottom: 0; }
  .message-text.markdown :global(h1) { font-size: 1.4em; font-weight: 700; margin: 12px 0 8px; border-bottom: 1px solid var(--border); padding-bottom: 4px; }
  .message-text.markdown :global(h2) { font-size: 1.2em; font-weight: 600; margin: 10px 0 6px; }
  .message-text.markdown :global(h3) { font-size: 1.1em; font-weight: 600; margin: 8px 0 4px; }
  .message-text.markdown :global(ul), .message-text.markdown :global(ol) { margin: 4px 0; padding-left: 20px; }
  .message-text.markdown :global(li) { margin: 2px 0; }
  .message-text.markdown :global(code) { background: rgba(0,0,0,0.3); padding: 1px 5px; border-radius: 3px; font-size: 0.9em; font-family: Consolas, Monaco, monospace; }
  .message-text.markdown :global(pre) { background: rgba(0,0,0,0.3); padding: 10px 12px; border-radius: 6px; overflow-x: auto; margin: 8px 0; }
  .message-text.markdown :global(pre code) { background: none; padding: 0; font-size: 0.85em; }
  .message-text.markdown :global(blockquote) { border-left: 3px solid var(--accent); padding-left: 12px; margin: 8px 0; color: var(--text-secondary); }
  .message-text.markdown :global(strong) { font-weight: 600; }
  .message-text.markdown :global(a) { color: var(--accent); text-decoration: underline; }
  .message-text.markdown :global(hr) { border: none; border-top: 1px solid var(--border); margin: 12px 0; }
  .input-form { display: flex; gap: 8px; padding: 12px 16px; border-top: 1px solid var(--border); }
  .input-form textarea { flex: 1; padding: 8px 12px; background: var(--bg-tertiary); border: 1px solid var(--border); border-radius: 6px; color: var(--text-primary); font-size: 13px; resize: none; font-family: inherit; }
  .btn { padding: 6px 14px; border: 1px solid var(--border); border-radius: 6px; background: var(--bg-tertiary); color: var(--text-primary); cursor: pointer; font-size: 13px; }
  .btn-stop { border-color: #da3633; color: #f97583; }
  .btn-stop:hover { background: #3d1f1f; }
  .btn-retry { color: var(--text-secondary); }
  .btn-retry:hover { color: var(--accent); border-color: var(--accent); }
  .input-actions { display: flex; gap: 6px; }
  .btn:disabled { opacity: 0.5; cursor: not-allowed; }
</style>
