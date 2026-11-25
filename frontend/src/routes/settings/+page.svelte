<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';

  type ModelInfo = { id: string; name?: string; contextWindow?: number; maxTokens?: number };
  type Provider = {
    id: string;
    name: string;
    kind: string;
    baseUrl: string | null;
    configured: boolean;
    keyLast4: string | null;
    enabled: boolean;
    models: ModelInfo[];
  };
  type Settings = {
    default: { provider: string; model: string; thinkingLevel: string };
    providers: Provider[];
    catalog: Array<{ id: string; name: string; models: ModelInfo[] }>;
    encryption: boolean;
  };

  const kinds = ['deepseek', 'openai', 'anthropic', 'google', 'openrouter', 'custom'];
  const thinkingLevels = ['off', 'low', 'medium', 'high'];

  let settings = $state<Settings | null>(null);
  let loading = $state(true);
  let error = $state('');
  let saving = $state(false);
  let testingId = $state<string | null>(null);
  let testResults = $state<Record<string, { ok: boolean; sample?: string; error?: string }>>({});

  let showForm = $state(false);
  let editingId = $state<string | null>(null);
  let form = $state({ id: '', name: '', kind: 'deepseek', baseUrl: '', apiKey: '', modelsText: '' });

  let defaultProvider = $state('');
  let defaultModel = $state('');
  let thinkingLevel = $state('off');
  let savingDefault = $state(false);

  let selectedModels = $derived.by(() => {
    if (!settings) return [] as ModelInfo[];
    return settings.providers.find((p) => p.id === defaultProvider)?.models
      ?? settings.catalog.find((c) => c.id === defaultProvider)?.models
      ?? [];
  });

  async function load() {
    loading = true;
    error = '';
    try {
      const loaded = (await api.getLlmSettings()) as Settings;
      settings = loaded;
      defaultProvider = loaded.default.provider;
      defaultModel = loaded.default.model;
      thinkingLevel = loaded.default.thinkingLevel;
    } catch (err: any) {
      error = err.message || 'Failed to load settings';
    } finally {
      loading = false;
    }
  }

  onMount(load);

  function resetForm() {
    form = { id: '', name: '', kind: 'deepseek', baseUrl: '', apiKey: '', modelsText: '' };
    editingId = null;
    showForm = false;
  }

  function startAdd() {
    resetForm();
    showForm = true;
  }

  function startEdit(provider: Provider) {
    form = {
      id: provider.id,
      name: provider.name,
      kind: provider.kind,
      baseUrl: provider.baseUrl ?? '',
      apiKey: '',
      modelsText: provider.models.map((m) => m.id).join(', '),
    };
    editingId = provider.id;
    showForm = true;
  }

  function changeKind(event: Event) {
    const kind = (event.target as HTMLSelectElement).value;
    form.kind = kind;
    if (!editingId && !form.id) form.id = kind;
  }

  async function saveProvider() {
    if (!form.id.trim()) {
      error = 'Provider id is required';
      return;
    }
    saving = true;
    error = '';
    try {
      const models = form.modelsText
        .split(',')
        .map((value) => value.trim())
        .filter(Boolean)
        .map((id) => ({ id }));

      await api.saveLlmProvider({
        id: form.id.trim(),
        name: form.name.trim() || form.id.trim(),
        kind: form.kind,
        baseUrl: form.baseUrl.trim() || undefined,
        apiKey: form.apiKey.trim() || undefined,
        models: models.length > 0 ? models : undefined,
      });
      resetForm();
      await load();
    } catch (err: any) {
      error = err.message || 'Failed to save provider';
    } finally {
      saving = false;
    }
  }

  async function removeProvider(provider: Provider) {
    if (!confirm(`Delete provider "${provider.name}"? Its stored key is removed.`)) return;
    try {
      await api.deleteLlmProvider(provider.id);
      await load();
    } catch (err: any) {
      error = err.message || 'Failed to delete provider';
    }
  }

  async function test(provider: Provider) {
    testingId = provider.id;
    testResults = { ...testResults, [provider.id]: { ok: false } };
    try {
      const result = await api.testLlmProvider(provider.id);
      testResults = { ...testResults, [provider.id]: { ok: true, sample: result.sample } };
    } catch (err: any) {
      testResults = { ...testResults, [provider.id]: { ok: false, error: err.message || 'Test failed' } };
    } finally {
      testingId = null;
    }
  }

  async function saveDefault() {
    savingDefault = true;
    error = '';
    try {
      await api.setLlmDefault({ provider: defaultProvider, modelId: defaultModel, thinkingLevel });
      await load();
    } catch (err: any) {
      error = err.message || 'Failed to save the default model';
    } finally {
      savingDefault = false;
    }
  }
</script>

<svelte:head>
  <title>Settings — Continue Story</title>
</svelte:head>

<div class="settings-page">
  <header class="settings-header">
    <a href="/" class="back-link">← Library</a>
    <h1>Settings</h1>
    <a href="/knowledge" class="ghost-link">Knowledge Base</a>
  </header>

  {#if loading}
    <div class="state">Loading settings…</div>
  {:else if !settings}
    <div class="state error">{error || 'Settings unavailable'}</div>
  {:else}
    <main class="settings-main">
      {#if error}
        <div class="banner error">{error}</div>
      {/if}

      {#if !settings.encryption}
        <div class="banner warn">
          <strong>Key storage is disabled.</strong>
          Set <code>CONFIG_ENCRYPTION_KEY</code> (32 bytes, base64) to store API keys from this page.
          Environment keys still work.
        </div>
      {/if}

      <section class="card">
        <div class="section-head">
          <div>
            <h2>Models &amp; Providers</h2>
            <p class="muted">Global provider list and default model. Used by every book.</p>
          </div>
          <button class="btn primary" onclick={startAdd}>+ Add provider</button>
        </div>

        {#if showForm}
          <div class="provider-form">
            <div class="field">
              <label for="provider-kind">Kind</label>
              <select id="provider-kind" value={form.kind} onchange={changeKind}>
                {#each kinds as kind}
                  <option value={kind}>{kind}</option>
                {/each}
              </select>
            </div>
            <div class="field">
              <label for="provider-id">ID</label>
              <input id="provider-id" bind:value={form.id} disabled={Boolean(editingId)} placeholder="deepseek" />
            </div>
            <div class="field">
              <label for="provider-name">Display name</label>
              <input id="provider-name" bind:value={form.name} placeholder="DeepSeek" />
            </div>
            <div class="field">
              <label for="provider-base">Base URL <span class="muted">(optional)</span></label>
              <input id="provider-base" bind:value={form.baseUrl} placeholder="https://api.example.com/v1" />
            </div>
            <div class="field">
              <label for="provider-key">API key {#if editingId}<span class="muted">(leave blank to keep)</span>{/if}</label>
              <input id="provider-key" type="password" bind:value={form.apiKey} autocomplete="off" placeholder="sk-…" />
            </div>
            {#if form.kind === 'custom'}
              <div class="field wide">
                <label for="provider-models">Model IDs <span class="muted">(comma separated)</span></label>
                <input id="provider-models" bind:value={form.modelsText} placeholder="qwen2.5-coder:7b, llama-3.1-8b" />
              </div>
            {/if}
            <div class="form-actions">
              <button class="btn primary" onclick={saveProvider} disabled={saving}>
                {saving ? 'Saving…' : 'Save provider'}
              </button>
              <button class="btn" onclick={resetForm} disabled={saving}>Cancel</button>
            </div>
          </div>
        {/if}

        <div class="provider-grid">
          {#each settings.providers as provider (provider.id)}
            <article class="provider-card">
              <div class="provider-top">
                <div class="provider-title">
                  <span class="provider-name">{provider.name}</span>
                  <span class="chip">{provider.kind}</span>
                </div>
                <span class:ok={provider.configured} class:off={!provider.configured} class="status">
                  {provider.configured ? 'configured' : 'no key'}
                </span>
              </div>
              <div class="provider-meta">
                <span>{provider.models.length} model{provider.models.length === 1 ? '' : 's'}</span>
                {#if provider.keyLast4}<span>key …{provider.keyLast4}</span>{/if}
                {#if provider.baseUrl}<span class="truncate">{provider.baseUrl}</span>{/if}
              </div>
              <div class="provider-actions">
                <button class="btn" onclick={() => test(provider)} disabled={testingId === provider.id}>
                  {testingId === provider.id ? 'Testing…' : 'Test'}
                </button>
                <button class="btn" onclick={() => startEdit(provider)}>Edit</button>
                <button class="btn danger" onclick={() => removeProvider(provider)}>Delete</button>
              </div>
              {#if testResults[provider.id]}
                <div class:ok={testResults[provider.id].ok} class:error={!testResults[provider.id].ok} class="test-result">
                  {#if testResults[provider.id].ok}
                    Connected — response: “{testResults[provider.id].sample || 'OK'}”
                  {:else}
                    {testResults[provider.id].error || 'Test failed'}
                  {/if}
                </div>
              {/if}
            </article>
          {/each}
          {#if settings.providers.length === 0}
            <p class="empty">No providers yet. Add one, or set an environment key.</p>
          {/if}
        </div>
      </section>

      <section class="card">
        <div class="section-head">
          <div>
            <h2>Default model</h2>
            <p class="muted">New conversations use this model. Saving reconfigures existing conversations.</p>
          </div>
        </div>

        <div class="default-grid">
          <div class="field">
            <label for="default-provider">Provider</label>
            <select
              id="default-provider"
              bind:value={defaultProvider}
              onchange={() => { defaultModel = selectedModels[0]?.id ?? ''; }}
            >
              {#each settings.providers as provider (provider.id)}
                <option value={provider.id}>{provider.name}</option>
              {/each}
            </select>
          </div>
          <div class="field">
            <label for="default-model">Model</label>
            <select id="default-model" bind:value={defaultModel}>
              {#each selectedModels as model (model.id)}
                <option value={model.id}>{model.name || model.id}</option>
              {/each}
            </select>
          </div>
          <div class="field">
            <label for="thinking-level">Thinking</label>
            <select id="thinking-level" bind:value={thinkingLevel}>
              {#each thinkingLevels as level}
                <option value={level}>{level}</option>
              {/each}
            </select>
          </div>
          <div class="field actions">
            <button class="btn primary" onclick={saveDefault} disabled={savingDefault || !defaultProvider || !defaultModel}>
              {savingDefault ? 'Saving…' : 'Save default'}
            </button>
          </div>
        </div>
      </section>
    </main>
  {/if}
</div>

<style>
  .settings-page {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100vh;
    height: 100dvh;
    overflow: hidden;
    background: var(--bg-primary);
    color: var(--text-primary);
  }

  .settings-header {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 14px 24px;
    border-bottom: 1px solid var(--border);
    background: var(--bg-secondary);
    flex-wrap: wrap;
  }

  .settings-header h1 {
    font-size: 18px;
    font-weight: 600;
    margin: 0;
  }

  .back-link,
  .ghost-link {
    color: var(--text-secondary);
    text-decoration: none;
    font-size: 13px;
  }

  .back-link:hover,
  .ghost-link:hover {
    color: var(--text-primary);
  }

  .ghost-link {
    margin-left: auto;
  }

  .settings-main {
    flex: 1;
    overflow-y: auto;
    padding: 24px;
    display: flex;
    flex-direction: column;
    gap: 20px;
    width: 100%;
    max-width: 1100px;
    margin: 0 auto;
  }

  .state {
    padding: 48px 24px;
    text-align: center;
    color: var(--text-secondary);
  }

  .state.error,
  .error {
    color: var(--error);
  }

  .banner {
    padding: 12px 16px;
    border-radius: 8px;
    font-size: 13px;
    border: 1px solid var(--border);
  }

  .banner.error {
    border-color: var(--error);
    background: rgba(248, 81, 73, 0.08);
  }

  .banner.warn {
    border-color: var(--warning);
    background: rgba(210, 153, 34, 0.08);
    color: var(--warning);
  }

  .banner code {
    background: var(--bg-tertiary);
    padding: 1px 5px;
    border-radius: 4px;
  }

  .card {
    background: var(--bg-secondary);
    border: 1px solid var(--border);
    border-radius: 12px;
    padding: 20px;
  }

  .section-head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 16px;
    margin-bottom: 16px;
    flex-wrap: wrap;
  }

  .section-head h2 {
    font-size: 16px;
    font-weight: 600;
    margin: 0 0 4px;
  }

  .muted {
    color: var(--text-secondary);
    font-size: 12px;
    font-weight: 400;
  }

  .provider-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
    gap: 12px;
  }

  .provider-card {
    border: 1px solid var(--border);
    border-radius: 10px;
    padding: 14px;
    background: var(--bg-primary);
    display: flex;
    flex-direction: column;
    gap: 10px;
    min-width: 0;
  }

  .provider-top {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }

  .provider-title {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }

  .provider-name {
    font-weight: 600;
    font-size: 14px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .chip {
    font-size: 10px;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    padding: 2px 6px;
    border-radius: 4px;
    background: var(--bg-tertiary);
    color: var(--text-secondary);
    flex-shrink: 0;
  }

  .status {
    font-size: 11px;
    padding: 2px 8px;
    border-radius: 999px;
    border: 1px solid var(--border);
    color: var(--text-secondary);
    flex-shrink: 0;
  }

  .status.ok {
    color: var(--success);
    border-color: var(--success);
  }

  .status.off {
    color: var(--text-secondary);
  }

  .provider-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 12px;
    font-size: 12px;
    color: var(--text-secondary);
  }

  .truncate {
    max-width: 100%;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .provider-actions {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
  }

  .test-result {
    font-size: 12px;
    color: var(--text-secondary);
    word-break: break-word;
  }

  .test-result.ok {
    color: var(--success);
  }

  .test-result.error {
    color: var(--error);
  }

  .empty {
    color: var(--text-secondary);
    font-size: 13px;
  }

  .provider-form,
  .default-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
    gap: 12px;
    padding: 16px;
    margin-bottom: 16px;
    border: 1px solid var(--border);
    border-radius: 10px;
    background: var(--bg-tertiary);
  }

  .field {
    display: flex;
    flex-direction: column;
    gap: 6px;
    min-width: 0;
  }

  .field.wide {
    grid-column: 1 / -1;
  }

  .field label {
    font-size: 12px;
    color: var(--text-secondary);
  }

  .field input,
  .field select {
    width: 100%;
    padding: 8px 10px;
    background: var(--bg-primary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 13px;
    outline: none;
  }

  .field input:focus,
  .field select:focus {
    border-color: var(--accent);
  }

  .field input:disabled {
    opacity: 0.6;
  }

  .form-actions {
    grid-column: 1 / -1;
    display: flex;
    gap: 8px;
  }

  .field.actions {
    justify-content: flex-end;
  }

  .btn {
    padding: 8px 14px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    font-size: 13px;
    cursor: pointer;
  }

  .btn:hover:not(:disabled) {
    border-color: var(--accent);
  }

  .btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .btn.primary {
    background: var(--accent);
    border-color: var(--accent);
    color: #fff;
  }

  .btn.danger:hover:not(:disabled) {
    border-color: var(--error);
    color: var(--error);
  }

  @media (max-width: 720px) {
    .settings-main {
      padding: 16px;
    }

    .settings-header {
      padding: 12px 16px;
      gap: 10px;
    }

    .ghost-link {
      margin-left: 0;
    }

    .provider-grid {
      grid-template-columns: 1fr;
    }

    .provider-form,
    .default-grid {
      grid-template-columns: 1fr;
    }

    .section-head {
      flex-direction: column;
    }

    .section-head .btn {
      width: 100%;
    }
  }
</style>
