<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import Icon from '$lib/components/Icon.svelte';

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
  let form = $state({ id: '', name: '', kind: 'deepseek', baseUrl: '', apiKey: '' });

  type DiscoveredModel = { id: string; name?: string; contextWindow?: number; maxTokens?: number };
  let discovered = $state<DiscoveredModel[]>([]);
  let selectedModelIds = $state<string[]>([]);
  let discovering = $state(false);
  let discoverError = $state('');
  let modelFilter = $state('');
  let manualModel = $state('');

  let providerFilter = $state('');
  let keyFor = $state<string | null>(null);
  let keyValue = $state('');
  let keySaving = $state(false);
  let keyError = $state('');

  let filteredProviders = $derived.by(() => {
    const q = providerFilter.trim().toLowerCase();
    const list = settings?.providers ?? [];
    return (q ? list.filter((p) => p.name.toLowerCase().includes(q) || p.id.toLowerCase().includes(q)) : list)
      .slice()
      .sort((a, b) => Number(b.configured) - Number(a.configured) || a.name.localeCompare(b.name));
  });

  let filteredDiscovered = $derived.by(() => {
    const q = modelFilter.trim().toLowerCase();
    if (!q) return discovered;
    return discovered.filter(
      (model) => model.id.toLowerCase().includes(q) || (model.name ?? '').toLowerCase().includes(q),
    );
  });

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

      const providerModels = loaded.providers.find((p) => p.id === defaultProvider)?.models ?? [];
      if (!providerModels.some((model) => model.id === defaultModel)) {
        defaultModel = providerModels[0]?.id ?? '';
      }
    } catch (err: any) {
      error = err.message || 'Failed to load settings';
    } finally {
      loading = false;
    }
  }

  onMount(load);

  function resetForm() {
    form = { id: '', name: '', kind: 'deepseek', baseUrl: '', apiKey: '' };
    discovered = [];
    selectedModelIds = [];
    discovering = false;
    discoverError = '';
    modelFilter = '';
    manualModel = '';
    editingId = null;
    showForm = false;
  }

  function prefillCatalog(kind: string) {
    const catalog = settings?.catalog.find((entry) => entry.id === kind);
    discovered = catalog ? catalog.models.map((model) => ({ ...model })) : [];
    selectedModelIds = discovered.map((model) => model.id);
  }

  async function loadModels(quiet = false) {
    if (discovering) return;
    discovering = true;
    if (!quiet) discoverError = '';
    try {
      const result = await api.discoverLlmModels({
        id: editingId ?? undefined,
        kind: form.kind,
        baseUrl: form.baseUrl.trim() || undefined,
        apiKey: form.apiKey.trim() || undefined,
      });
      discovered = result.models;
      const kept = selectedModelIds.filter((id) => discovered.some((model) => model.id === id));
      selectedModelIds = kept.length > 0 ? kept : discovered.map((model) => model.id);
      discoverError = '';
    } catch (err: any) {
      if (!quiet) discoverError = err.message || 'Could not load models';
    } finally {
      discovering = false;
    }
  }

  function toggleModel(id: string) {
    selectedModelIds = selectedModelIds.includes(id)
      ? selectedModelIds.filter((value) => value !== id)
      : [...selectedModelIds, id];
  }

  function selectAllModels() {
    selectedModelIds = discovered.map((model) => model.id);
  }

  function addManualModel() {
    const id = manualModel.trim();
    if (!id) return;
    if (!discovered.some((model) => model.id === id)) {
      discovered = [...discovered, { id, name: id }];
    }
    if (!selectedModelIds.includes(id)) selectedModelIds = [...selectedModelIds, id];
    manualModel = '';
  }

  function startAdd() {
    resetForm();
    showForm = true;
    prefillCatalog(form.kind);
  }

  function startEdit(provider: Provider) {
    form = {
      id: provider.id,
      name: provider.name,
      kind: provider.kind as typeof form.kind,
      baseUrl: provider.baseUrl ?? '',
      apiKey: '',
    };
    editingId = provider.id;
    showForm = true;
    discovered = provider.models.map((model) => ({ ...model }));
    selectedModelIds = provider.models.map((model) => model.id);
    discoverError = '';
    modelFilter = '';
    manualModel = '';
    // Refresh the live list from the provider using the stored key.
    void loadModels(true);
  }

  function changeKind(event: Event) {
    const kind = (event.target as HTMLSelectElement).value;
    form.kind = kind;
    if (!editingId && !form.id) form.id = kind;
    if (!editingId) prefillCatalog(kind);
  }

  async function saveProvider() {
    if (!form.id.trim()) {
      error = 'Provider id is required';
      return;
    }
    if (discovered.length > 0 && selectedModelIds.length === 0) {
      error = 'Select at least one model to expose for this provider';
      return;
    }

    saving = true;
    error = '';
    try {
      const models = selectedModelIds.map((id) => {
        const found = discovered.find((model) => model.id === id);
        return { id, name: found?.name };
      });

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

  async function useProvider(provider: Provider) {
    if (provider.models.length === 0) {
      error = 'This provider has no models selected yet';
      return;
    }
    defaultProvider = provider.id;
    defaultModel = provider.models[0].id;
    await saveDefault();
  }

  function toggleKey(id: string) {
    keyFor = keyFor === id ? null : id;
    keyValue = '';
    keyError = '';
  }

  async function saveKey(id: string) {
    const provider = settings?.providers.find((p) => p.id === id);
    if (!provider || !keyValue.trim()) return;
    keySaving = true;
    keyError = '';
    try {
      await api.saveLlmProvider({
        id,
        name: provider.name,
        kind: provider.kind,
        baseUrl: provider.baseUrl ?? undefined,
        apiKey: keyValue.trim(),
      });
      keyFor = null;
      keyValue = '';
      await load();
    } catch (err: any) {
      keyError = err.message || 'Failed to save key';
    } finally {
      keySaving = false;
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
    <a href="/" class="back-link" title="Back to library" aria-label="Back to library">
      <Icon name="back" size={18} />
    </a>
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
              <input
                id="provider-key"
                type="password"
                bind:value={form.apiKey}
                autocomplete="off"
                placeholder="sk-…"
                onblur={() => { if (form.apiKey.trim() && discovered.length === 0) void loadModels(true); }}
              />
            </div>
            <div class="field wide models-field">
              <div class="models-head">
                <div>
                  <label for="provider-models">Models</label>
                  <p class="muted small">Loaded from the provider — choose which ones to expose.</p>
                </div>
                <button class="btn small" type="button" onclick={() => loadModels()} disabled={discovering}>
                  {discovering ? 'Loading…' : 'Load models'}
                </button>
              </div>

              {#if discoverError}
                <p class="field-error">{discoverError}</p>
              {/if}

              {#if discovered.length > 0}
                <div class="models-toolbar">
                  <input class="model-search" bind:value={modelFilter} placeholder="Filter models…" />
                  <button class="link-btn" type="button" onclick={selectAllModels}>All</button>
                  <button class="link-btn" type="button" onclick={() => (selectedModelIds = [])}>None</button>
                </div>
                <div class="model-list">
                  {#each filteredDiscovered as model (model.id)}
                    <label class="model-row" class:selected={selectedModelIds.includes(model.id)}>
                      <input
                        type="checkbox"
                        checked={selectedModelIds.includes(model.id)}
                        onchange={() => toggleModel(model.id)}
                      />
                      <span class="model-id">{model.id}</span>
                      {#if model.name && model.name !== model.id}
                        <span class="model-name">{model.name}</span>
                      {/if}
                      {#if model.contextWindow}
                        <span class="model-ctx">{Math.round(model.contextWindow / 1000)}k</span>
                      {/if}
                    </label>
                  {/each}
                  {#if filteredDiscovered.length === 0}
                    <p class="model-empty">No models match “{modelFilter}”.</p>
                  {/if}
                </div>
                <p class="muted small">{selectedModelIds.length} of {discovered.length} selected</p>
              {:else}
                <p class="muted small">Add the API key, then load the list. No manual model IDs needed.</p>
              {/if}

              <div class="manual-model">
                <input
                  bind:value={manualModel}
                  placeholder="Or add a model ID manually"
                  onkeydown={(e) => { if (e.key === 'Enter') { e.preventDefault(); addManualModel(); } }}
                />
                <button class="btn" type="button" onclick={addManualModel} disabled={!manualModel.trim()}>Add</button>
              </div>
            </div>
            <div class="form-actions">
              <button class="btn primary" onclick={saveProvider} disabled={saving}>
                {saving ? 'Saving…' : 'Save provider'}
              </button>
              <button class="btn" onclick={resetForm} disabled={saving}>Cancel</button>
            </div>
          </div>
        {/if}

        <div class="provider-tools">
          <input class="provider-filter" bind:value={providerFilter} placeholder="Filter providers…" />
          <span class="muted">{filteredProviders.length} provider{filteredProviders.length === 1 ? '' : 's'}</span>
        </div>

        <div class="provider-list">
          {#each filteredProviders as provider (provider.id)}
            <div class="provider-row">
              <div class="provider-main">
                <div class="provider-name-line">
                  <span class="provider-name">{provider.name}</span>
                  <span class="mono muted small">{provider.id} · {provider.models.length} model{provider.models.length === 1 ? '' : 's'}</span>
                  {#if provider.keyLast4}<span class="mono muted small">key …{provider.keyLast4}</span>{/if}
                </div>
                {#if provider.baseUrl}
                  <div class="mono muted small truncate">{provider.baseUrl}</div>
                {/if}
                {#if testResults[provider.id]}
                  <div class:ok={testResults[provider.id].ok} class:error={!testResults[provider.id].ok} class="test-result">
                    {#if testResults[provider.id].ok}
                      Connected — response: “{testResults[provider.id].sample || 'OK'}”
                    {:else}
                      {testResults[provider.id].error || 'Test failed'}
                    {/if}
                  </div>
                {/if}
              </div>

              <span class:ok={provider.configured} class:off={!provider.configured} class="status-pill">
                {provider.configured ? 'ready' : 'not set'}
              </span>

              <div class="provider-actions">
                <button class="btn small" onclick={() => test(provider)} disabled={testingId === provider.id}>
                  {testingId === provider.id ? 'Testing…' : 'Test'}
                </button>
                <button class="btn small" onclick={() => useProvider(provider)} disabled={provider.models.length === 0}>Use</button>
                <button class="btn small" onclick={() => startEdit(provider)}>Models</button>
                <button class="btn small" onclick={() => toggleKey(provider.id)}>
                  {keyFor === provider.id ? 'Cancel' : provider.configured ? 'Change key' : 'Add key'}
                </button>
                <button class="btn small danger" onclick={() => removeProvider(provider)}>Delete</button>
              </div>

              {#if keyFor === provider.id}
                <form
                  class="key-row"
                  onsubmit={(e) => { e.preventDefault(); saveKey(provider.id); }}
                >
                  <input
                    type="password"
                    bind:value={keyValue}
                    placeholder="API key"
                    autocomplete="off"
                    autofocus
                  />
                  <button class="btn primary small" type="submit" disabled={keySaving || !keyValue.trim()}>
                    {keySaving ? 'Saving…' : 'Save key'}
                  </button>
                  {#if keyError}<span class="field-error">{keyError}</span>{/if}
                </form>
              {/if}
            </div>
          {/each}

          {#if filteredProviders.length === 0}
            <p class="empty">{settings.providers.length === 0 ? 'No providers yet. Add one, or set an environment key.' : 'No providers match this filter.'}</p>
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
    background: var(--warning-bg);
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

  .settings-header {
    height: 64px;
    padding: 0 20px;
    gap: 12px;
    background: rgba(251, 247, 240, 0.86);
    backdrop-filter: blur(14px);
    border-bottom: 1px solid var(--border);
  }

  .settings-header h1 {
    font-size: 22px;
  }

  .back-link {
    width: 36px;
    height: 36px;
    display: flex;
    align-items: center;
    justify-content: center;
    border-radius: 10px;
    color: var(--text-secondary);
    text-decoration: none;
  }

  .back-link:hover {
    background: var(--bg-secondary);
    color: var(--text-primary);
  }

  .ghost-link {
    margin-left: auto;
    height: 34px;
    padding: 0 14px;
    display: flex;
    align-items: center;
    border: 1px solid var(--border);
    border-radius: var(--r-full);
    color: var(--text-secondary);
    text-decoration: none;
    font-size: 12.5px;
    font-weight: 500;
  }

  .ghost-link:hover {
    color: var(--text-primary);
    border-color: var(--border-strong);
  }

  .small {
    font-size: 11.5px;
  }

  .models-field {
    border-top: 1px dashed var(--border);
    padding-top: 14px;
    margin-top: 2px;
  }

  .models-head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 10px;
  }

  .models-head .muted {
    margin-top: 2px;
  }

  .btn.small {
    height: 32px;
    padding: 0 12px;
    font-size: 12px;
  }

  .models-toolbar {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 8px;
  }

  .model-search {
    flex: 1;
    height: 34px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--bg-elevated);
    font-size: 12.5px;
  }

  .link-btn {
    border: none;
    background: none;
    color: var(--accent);
    font-size: 12px;
    font-weight: 600;
    padding: 4px;
  }

  .model-list {
    max-height: 230px;
    overflow-y: auto;
    border: 1px solid var(--border);
    border-radius: 10px;
    background: var(--bg-elevated);
  }

  .model-row {
    display: flex;
    align-items: center;
    gap: 9px;
    padding: 8px 12px;
    border-bottom: 1px solid var(--border);
    cursor: pointer;
    font-size: 12.5px;
  }

  .model-row:last-child {
    border-bottom: none;
  }

  .model-row.selected {
    background: var(--accent-soft);
  }

  .model-row input {
    width: 15px;
    height: 15px;
    accent-color: var(--accent);
    flex-shrink: 0;
  }

  .model-id {
    font-family: var(--font-mono);
    font-size: 11.5px;
  }

  .model-name {
    flex: 1;
    min-width: 0;
    color: var(--text-secondary);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .model-ctx {
    margin-left: auto;
    color: var(--text-muted);
    font-size: 10.5px;
  }

  .model-empty {
    padding: 14px;
    text-align: center;
    color: var(--text-muted);
    font-size: 12px;
  }

  .manual-model {
    display: flex;
    gap: 8px;
    margin-top: 8px;
  }

  .manual-model input {
    flex: 1;
    height: 34px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--bg-elevated);
    font-size: 12.5px;
  }

  .field-error {
    margin: 2px 0 6px;
    color: var(--error);
    font-size: 12px;
  }

  .provider-tools {
    display: flex;
    align-items: center;
    gap: 10px;
    margin: 12px 0 8px;
  }

  .provider-filter {
    flex: 1;
    height: 38px;
    padding: 0 11px;
    border: 1px solid var(--border);
    border-radius: 10px;
    background: var(--bg-elevated);
    font-size: 13px;
  }

  .provider-list {
    border: 1px solid var(--border);
    border-radius: 12px;
    background: var(--bg-elevated);
    overflow: hidden;
  }

  .provider-row {
    display: flex;
    align-items: center;
    gap: 12px;
    flex-wrap: wrap;
    padding: 11px 14px;
    border-bottom: 1px solid var(--border);
  }

  .provider-row:last-child {
    border-bottom: none;
  }

  .provider-main {
    flex: 1;
    min-width: 180px;
  }

  .provider-name-line {
    display: flex;
    align-items: baseline;
    gap: 8px;
    flex-wrap: wrap;
  }

  .provider-name {
    font-weight: 600;
    font-size: 14px;
  }

  .mono {
    font-family: var(--font-mono);
  }

  .small {
    font-size: 11.5px;
  }

  .status-pill {
    display: inline-flex;
    align-items: center;
    padding: 2px 9px;
    border-radius: 99px;
    font-family: var(--font-mono);
    font-size: 10.5px;
    font-weight: 600;
    background: var(--bg-secondary);
    color: var(--text-muted);
  }

  .status-pill.ok {
    background: var(--success-bg);
    color: var(--success);
  }

  .provider-actions {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
  }

  .btn.small {
    height: 30px;
    padding: 0 10px;
    font-size: 12px;
  }

  .key-row {
    flex-basis: 100%;
    display: flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
    padding-top: 4px;
  }

  .key-row input {
    flex: 1;
    min-width: 200px;
    height: 34px;
    padding: 0 10px;
    border: 1px solid var(--border);
    border-radius: 8px;
    background: var(--bg-primary);
    font-family: var(--font-mono);
    font-size: 12.5px;
  }

  .provider-list .empty {
    padding: 24px;
    text-align: center;
    color: var(--text-secondary);
    font-size: 13px;
  }
</style>
