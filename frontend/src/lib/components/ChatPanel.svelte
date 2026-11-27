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
  let stickToBottom = true;
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

  // Model picker (per conversation) — options come from the LLM settings.
  let llmSettings = $state<any>(null);
  let selectedModel = $state('');
  let modelSaving = $state(false);
  let modelGroups = $derived.by(() => {
    const groups: Array<[string, any[]]> = [];
    for (const provider of llmSettings?.providers ?? []) {
      if (provider.configured && provider.models?.length) groups.push([provider.id, provider.models]);
    }
    return groups;
  });

  let suggestions = $derived(
    mode === 'knowledge'
      ? [
          { k: '🔎', t: 'Find gaps', d: 'what the notes are missing', p: 'Review the shared knowledge base and list the most important gaps, with a short note on why each matters.' },
          { k: '🧵', t: 'Connect entries', d: 'find links between topics', p: 'Look through the shared knowledge base and point out connections between entries that are not yet linked.' },
          { k: '📝', t: 'Summarize a category', d: 'pull the key facts together', p: 'Summarize the key facts in the most populated knowledge base category, with links to the entries.' },
          { k: '⚖️', t: 'Check contradictions', d: 'where sources disagree', p: 'Scan the shared knowledge base for contradictions or duplicated facts and list them plainly.' },
        ]
      : [
          { k: '📖', t: 'Continue the chapter', d: 'pick up where the prose stops', p: 'Continue the current chapter from where it stops. Match the established voice and keep the scene moving.' },
          { k: '✂️', t: 'Tighten the prose', d: 'cut filler, sharpen rhythm', p: 'Tighten the prose in the current chapter. Cut filler and repetition and sharpen the rhythm, but keep every event and line of dialogue.' },
          { k: '🧭', t: "What's next?", d: 'three directions for the next scene', p: 'Give me three concrete directions the next scene could take, with the trade-offs of each. Do not write the scene yet.' },
          { k: '🪄', t: 'Critique the chapter', d: 'pacing, voice, continuity', p: "Critique the current chapter: pacing, voice, dialogue, and continuity. Be specific and honest, then name the top three fixes." },
        ],
  );
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
    const _tools = completedTools.length + activeTools.length;
    const el = chatContainer;
    if (el && stickToBottom) el.scrollTop = el.scrollHeight;
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

    if (mode === 'book') {
      try {
        llmSettings = await api.getLlmSettings();
        let model = '';
        try {
          const info = await api.getChatModel(slug);
          model = `${info.model.provider}/${info.model.modelId}`;
        } catch {
          // Fall back to the stored/default model.
        }
        if (!model) {
          const stored = localStorage.getItem(`chat-model-${slug}`);
          model = stored || `${llmSettings.default.provider}/${llmSettings.default.model}`;
        }
        selectedModel = model;
      } catch {
        // Model picker stays hidden if settings are unavailable.
      }
    }
  });

  function sendSuggestion(prompt: string) {
    void send(prompt);
  }

  async function changeModel(event: Event) {
    const value = (event.target as HTMLSelectElement).value;
    if (!value) return;
    const [provider, ...rest] = value.split('/');
    const modelId = rest.join('/');
    selectedModel = value;
    try { localStorage.setItem(`chat-model-${slug}`, value); } catch { }
    if (mode !== 'book') return;
    modelSaving = true;
    try {
      await api.setChatModel(slug, provider, modelId);
    } catch (err: any) {
      chatError = err.message || 'Could not switch model';
    } finally {
      modelSaving = false;
    }
  }

  async function send(textOverride?: string) {
    const msg = (textOverride ?? input).trim();
    if (!msg || streaming) return;

    chatError = '';
    messages = [...messages, { role: 'user', text: msg, createdAt: Date.now() }];
    input = '';
    stickToBottom = true;
    requestAnimationFrame(() => {
      if (chatContainer) chatContainer.scrollTop = chatContainer.scrollHeight;
    });
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

  <div
    class="transcript"
    bind:this={chatContainer}
    aria-live="polite"
    onscroll={(e) => {
      const el = e.currentTarget;
      stickToBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    }}
  >
    <div class="transcript-inner">
      {#if messages.length === 0 && !streaming}
        <div class="hero">
          <h1>
            {#if mode === 'knowledge'}
              What should we <em>research</em>?
            {:else}
              What should we <em>write</em>?
            {/if}
          </h1>
          <p>
            {#if mode === 'knowledge'}
              Ask about the shared knowledge base, connect entries, or send the assistant to fill the gaps.
            {:else}
              Ask for a continuation, a rewrite, a critique. The assistant can read the book, check the wiki, and edit chapters.
            {/if}
          </p>
          <div class="suggestions">
            {#each suggestions as item}
              <button type="button" class="suggestion" onclick={() => sendSuggestion(item.p)}>
                <div class="k">{item.k}</div>
                <div class="t">{item.t}</div>
                <div class="d">{item.d}</div>
              </button>
            {/each}
          </div>
        </div>
      {/if}

      {#each messages as msg}
        {#if msg.role === 'user'}
          <div class="msg-user">
            <div class="author">
              You
              {#if msg.createdAt}<span class="when">{formatTime(msg.createdAt)}</span>{/if}
            </div>
            <div class="body">{msg.text}</div>
          </div>
        {:else}
          <div class="msg-assistant">
            {#if msg.thinking}
              <details class="thinking">
                <summary>thinking</summary>
                <pre>{msg.thinking}</pre>
              </details>
            {/if}
            <div class="prose markdown">{@html renderMarkdown(msg.text)}</div>
            <button class="copy-btn" onclick={() => copyMessage(msg.text)} title="Copy message">
              {copiedText === msg.text ? 'Copied' : 'Copy'}
            </button>
          </div>
        {/if}
      {/each}

      {#if completedTools.length > 0 || activeTools.length > 0}
        <div class="msg-assistant">
          {#each completedTools as tool (tool.id)}
            {@const activity = describeToolActivity(tool.name, tool.args)}
            <details class="proc">
              <summary class="proc-head">
                <span class="st ok">✓</span>
                <span class="name">{tool.name}</span>
                <span class="arg">{activity.label}</span>
                <span class="chev">▾</span>
              </summary>
              {#if tool.result}
                <div class="proc-body"><pre>{tool.result}</pre></div>
              {/if}
            </details>
          {/each}
          {#each activeTools as tool (tool.id)}
            {@const activity = describeToolActivity(tool.name, tool.args)}
            <div class="proc running">
              <div class="proc-head">
                <span class="st run">●</span>
                <span class="name">{tool.name}</span>
                <span class="arg">{activity.label}</span>
              </div>
            </div>
          {/each}
        </div>
      {/if}

      {#if streaming && displayResponse}
        <div class="msg-assistant">
          <div class="prose markdown">{@html renderMarkdown(displayResponse)}<span class="cursor-blink"></span></div>
        </div>
      {/if}

      {#if chatError}
        <div class="notice error">⚠ {chatError}</div>
      {/if}
    </div>
  </div>

  <div class="composer-wrap">
    <div class="status-line">
      {#if streaming}<span class="live">● working</span>{/if}
      {#if modelSaving}<span>switching model…</span>{/if}
    </div>
    <form class="composer" onsubmit={(e) => { e.preventDefault(); send(); }}>
      <textarea
        bind:this={textareaEl}
        bind:value={input}
        placeholder={mode === 'knowledge' ? 'Ask the research assistant…' : 'Ask the assistant to write, edit, or check something…'}
        disabled={streaming}
        onkeydown={handleKeydown}
        oninput={autoResize}
        rows="1"
      ></textarea>
      <div class="bar">
        {#if mode === 'book' && modelGroups.length > 0}
          <select
            class="model-select"
            bind:value={selectedModel}
            onchange={changeModel}
            title="Model for this conversation"
          >
            {#if selectedModel && !modelGroups.some(([provider, models]) => models.some((model) => `${provider}/${model.id}` === selectedModel))}
              <option value={selectedModel}>{selectedModel}</option>
            {/if}
            {#each modelGroups as [provider, models]}
              <optgroup label={provider}>
                {#each models as model}
                  <option value={`${provider}/${model.id}`}>{model.name || model.id}</option>
                {/each}
              </optgroup>
            {/each}
          </select>
        {/if}
        <span class="grow"></span>
        {#if streaming}
          <button type="button" class="btn" onclick={stop} title="Stop the response">Stop</button>
        {:else if mode === 'book' && messages.length > 0}
          <button type="button" class="btn" onclick={retry} title="Retry the last message">Retry</button>
        {/if}
        <button type="submit" class="btn primary" disabled={streaming || !input.trim()}>
          Send ↵
        </button>
      </div>
    </form>
  </div>
</div>

<style>

  .chat-panel {
    display: flex;
    flex-direction: column;
    height: 100%;
    min-height: 0;
    background: var(--bg-primary);
  }

  .panel-header {
    flex-shrink: 0;
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 10px;
    padding: 13px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-elevated);
  }

  .panel-heading {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
  }

  .assistant-badge {
    width: 32px;
    height: 32px;
    border-radius: 10px;
    background: var(--accent-soft);
    color: var(--accent);
    display: flex;
    align-items: center;
    justify-content: center;
    flex-shrink: 0;
  }

  .panel-title {
    font-size: 15px;
    line-height: 1.15;
  }

  .panel-sub {
    font-size: 10.5px;
    color: var(--text-muted);
    margin-top: 1px;
  }

  .session-controls {
    display: flex;
    gap: 4px;
    align-items: center;
  }

  .icon-btn {
    width: 30px;
    height: 30px;
    display: flex;
    align-items: center;
    justify-content: center;
    border: none;
    background: transparent;
    color: var(--text-secondary);
    border-radius: 8px;
  }

  .icon-btn:hover {
    background: var(--bg-secondary);
    color: var(--text-primary);
  }

  .transcript {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 22px 18px 32px;
  }

  .transcript-inner {
    max-width: 760px;
    margin: 0 auto;
    display: flex;
    flex-direction: column;
    gap: 18px;
  }

  .hero {
    margin: 4vh auto 0;
    max-width: 620px;
  }

  .hero h1 {
    font-family: var(--font-serif);
    font-size: clamp(28px, 6vw, 42px);
    line-height: 1.08;
    letter-spacing: -0.02em;
    color: var(--text-primary);
  }

  .hero h1 em {
    color: var(--accent);
    font-style: italic;
  }

  .hero p {
    color: var(--text-secondary);
    font-size: 14px;
    line-height: 1.65;
    margin-top: 9px;
  }

  .suggestions {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
    gap: 10px;
    margin-top: 18px;
  }

  .suggestion {
    text-align: left;
    border: 1px solid var(--border);
    background: var(--bg-elevated);
    border-radius: 12px;
    padding: 12px 13px;
    transition: transform 0.12s, border-color 0.12s, box-shadow 0.12s;
  }

  .suggestion:hover {
    transform: translateY(-2px);
    border-color: var(--border-strong);
    box-shadow: var(--shadow-sm);
  }

  .suggestion .k {
    font-size: 18px;
    line-height: 1;
  }

  .suggestion .t {
    font-weight: 600;
    font-size: 13.5px;
    margin-top: 5px;
  }

  .suggestion .d {
    font-size: 11.5px;
    color: var(--text-secondary);
    margin-top: 1px;
  }

  .msg-user {
    padding: 2px 0 2px 14px;
    border-left: 3px solid var(--accent);
  }

  .msg-user .author {
    font-size: 10px;
    font-weight: 700;
    letter-spacing: 0.09em;
    text-transform: uppercase;
    color: var(--accent);
    margin-bottom: 3px;
  }

  .msg-user .when {
    margin-left: 6px;
    font-weight: 400;
    letter-spacing: 0;
    text-transform: none;
    color: var(--text-muted);
  }

  .msg-user .body {
    font-size: 15.5px;
    line-height: 1.6;
    white-space: pre-wrap;
    word-break: break-word;
  }

  .msg-assistant {
    min-width: 0;
  }

  .msg-assistant .prose {
    font-size: 14.5px;
    line-height: 1.68;
    color: var(--text-primary);
    overflow-wrap: anywhere;
  }

  .prose > :first-child { margin-top: 0; }
  .prose > :last-child { margin-bottom: 0; }
  .prose p { margin: 0.55em 0; }
  .prose h1, .prose h2, .prose h3 {
    font-family: var(--font-serif);
    line-height: 1.2;
    margin: 1.1em 0 0.4em;
  }
  .prose h1 { font-size: 1.5em; }
  .prose h2 { font-size: 1.3em; }
  .prose h3 { font-size: 1.1em; }
  .prose ul, .prose ol { padding-left: 1.3em; margin: 0.4em 0; }
  .prose li { margin: 0.2em 0; }
  .prose code {
    font-family: var(--font-mono);
    font-size: 0.86em;
    background: var(--bg-secondary);
    padding: 1px 5px;
    border-radius: 4px;
  }
  .prose pre {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    padding: 12px 14px;
    border-radius: 8px;
    overflow-x: auto;
    margin: 0.6em 0;
    font-size: 12.5px;
    line-height: 1.5;
  }
  .prose pre code { background: none; padding: 0; font-size: inherit; }
  .prose a { color: var(--accent); text-underline-offset: 2px; }
  .prose blockquote {
    margin: 0.6em 0;
    padding-left: 12px;
    border-left: 3px solid var(--border-strong);
    color: var(--text-secondary);
  }
  .prose table {
    border-collapse: collapse;
    font-size: 13px;
    display: block;
    max-width: 100%;
    overflow-x: auto;
  }
  .prose th, .prose td { border: 1px solid var(--border); padding: 4px 10px; }
  .prose th { background: var(--bg-secondary); }
  .prose hr { border: none; border-top: 1px solid var(--border); margin: 12px 0; }
  .prose strong { font-weight: 600; }

  .thinking {
    font-size: 12px;
    color: var(--text-secondary);
    border-left: 2px dashed var(--border-strong);
    padding: 2px 0 2px 10px;
    margin: 6px 0;
    max-height: 180px;
    overflow: auto;
  }

  .thinking summary {
    cursor: pointer;
    font-size: 10px;
    font-weight: 700;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--text-muted);
  }

  .thinking pre {
    margin: 6px 0 0;
    white-space: pre-wrap;
    font-family: var(--font-mono);
    font-size: 11px;
    line-height: 1.5;
  }

  .copy-btn {
    margin-top: 5px;
    background: none;
    border: none;
    padding: 2px 0;
    color: var(--text-muted);
    font-size: 10.5px;
    cursor: pointer;
  }

  .copy-btn:hover {
    color: var(--accent);
  }

  .cursor-blink::after {
    content: '▍';
    color: var(--accent);
    animation: blink 1s steps(2) infinite;
    margin-left: 1px;
  }

  @keyframes blink { 50% { opacity: 0; } }

  .proc {
    border: 1px solid var(--border);
    background: var(--bg-elevated);
    border-radius: 9px;
    overflow: hidden;
    margin: 6px 0;
    font-size: 12.5px;
  }

  .proc-head {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 7px 10px;
    cursor: pointer;
    user-select: none;
    list-style: none;
  }

  .proc-head::-webkit-details-marker { display: none; }

  .proc-head:hover {
    background: var(--bg-secondary);
  }

  .proc-head .st {
    width: 14px;
    text-align: center;
    font-weight: 700;
    font-family: var(--font-mono);
    font-size: 11px;
  }

  .proc-head .st.ok { color: var(--success); }
  .proc-head .st.run {
    color: var(--accent);
    animation: pulse 1s ease-in-out infinite alternate;
  }

  @keyframes pulse { from { opacity: 0.35; } }

  .proc-head .name {
    font-family: var(--font-mono);
    font-size: 11.5px;
    font-weight: 600;
  }

  .proc-head .arg {
    flex: 1;
    min-width: 0;
    font-family: var(--font-mono);
    font-size: 11px;
    color: var(--text-secondary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .proc-head .chev {
    font-size: 10px;
    color: var(--text-muted);
  }

  .proc-body {
    border-top: 1px solid var(--border);
    background: var(--bg-primary);
  }

  .proc-body pre {
    margin: 0;
    padding: 10px 11px;
    font-family: var(--font-mono);
    font-size: 11px;
    line-height: 1.55;
    white-space: pre-wrap;
    word-break: break-word;
    max-height: 280px;
    overflow: auto;
  }

  .notice {
    align-self: center;
    font-size: 11.5px;
    color: var(--text-secondary);
    background: var(--bg-secondary);
    padding: 5px 12px;
    border-radius: 99px;
    max-width: 90%;
    text-align: center;
  }

  .notice.error {
    color: var(--error);
    background: var(--error-bg);
  }

  .composer-wrap {
    flex-shrink: 0;
    padding: 0 16px 16px;
  }

  .status-line {
    max-width: 760px;
    margin: 0 auto 6px;
    min-height: 16px;
    display: flex;
    gap: 8px;
    align-items: center;
    font-size: 11px;
    color: var(--text-muted);
  }

  .status-line .live { color: var(--accent); }

  .composer {
    max-width: 760px;
    margin: 0 auto;
    background: var(--bg-elevated);
    border: 1px solid var(--border-strong);
    border-radius: 14px;
    box-shadow: var(--shadow-sm);
    transition: border-color 0.15s;
  }

  .composer:focus-within {
    border-color: var(--accent);
  }

  .composer textarea {
    width: 100%;
    border: 0;
    outline: none;
    resize: none;
    padding: 12px 14px 4px;
    background: transparent;
    color: var(--text-primary);
    font-size: 15px;
    line-height: 1.5;
    min-height: 44px;
    max-height: 260px;
    overflow-y: auto;
  }

  .composer .bar {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 8px 8px 10px;
  }

  .model-select {
    max-width: 220px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--bg-primary);
    color: var(--text-secondary);
    font-size: 11.5px;
    padding: 4px 6px;
  }

  .grow { flex: 1; }

  .composer .btn {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    height: 34px;
    padding: 0 13px;
    border: 1px solid var(--border);
    border-radius: 9px;
    background: var(--bg-primary);
    color: var(--text-primary);
    font-size: 12.5px;
    font-weight: 500;
  }

  .composer .btn:hover:not(:disabled) {
    border-color: var(--border-strong);
  }

  .composer .btn.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: #fff;
  }

  .composer .btn.primary:hover:not(:disabled) {
    background: var(--accent-hover);
  }

  .composer .btn:disabled {
    opacity: 0.45;
    cursor: not-allowed;
  }

  @media (max-width: 600px) {
    .transcript { padding: 16px 12px 24px; }
    .composer-wrap { padding: 0 12px 12px; }
    .model-select { max-width: 150px; }
    .hero h1 { font-size: 30px; }
    .suggestions { grid-template-columns: 1fr; }
  }
</style>
