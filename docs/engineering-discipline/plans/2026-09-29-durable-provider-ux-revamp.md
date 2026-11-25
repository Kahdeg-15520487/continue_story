# Durable Harness + Provider Config + UX Revamp — Design & Migration Plan

- **Date:** 2026-09-29
- **Status:** Proposed (plan only — no code yet)
- **Scope:** `agent/` (rewrite), `backend/` (contract + settings), `frontend/` (transport + full UX revamp), `k8s/`
- **Decisions locked by owner:**
  1. Deliver a **plan first**; implementation starts only after sign-off.
  2. pi-durable migration is a **full transport rewrite** — expose pi-durable `watch`/ops and update backend + frontend to the new contract (no compatibility shim).
  3. Visual direction: **light, minimal writing studio** (paper white, restrained typography) **+ responsive**.
  4. Provider config: **global provider list + default model, encrypted API keys, test-connection, settings page** (no per-book override in this scope).
  5. Deploy strategy: **work directly on `master`**, but **pause ArgoCD auto-sync** for `continue-story` during the rewrite and sync manually at phase gates (auto-sync + selfHeal restored after cutover).

> ✅ **Decision 5 resolved:** auto-sync is **paused for the duration of the rewrite**. `master` still receives commits, but ArgoCD does not roll them out; at each phase gate we run a manual sync and verify, then restore `automated: {prune, selfHeal}` after cutover. This keeps the "work on master" choice without exposing half-migrated transports.
>
> **Pause** (ArgoCD Application is declared in the `k3s_lab` repo at `argocd/applications.yaml` — if that repo is itself app-of-apps managed, edit the source manifest there instead of patching live, or the parent's selfHeal will revert the patch):
> ```bash
> # preferred: edit k3s_lab/argocd/applications.yaml, comment out syncPolicy.automated
> # or live (verify it sticks):
> kubectl -n argocd patch application continue-story --type merge \
>   -p '{"spec":{"syncPolicy":{"automated":null}}}'
> ```
> **Resume after cutover:**
> ```bash
> kubectl -n argocd patch application continue-story --type merge \
>   -p '{"spec":{"syncPolicy":{"automated":{"prune":true,"selfHeal":true}}}}'
> ```
> Manual phase-gate sync: `argocd app sync continue-story` (or the UI) after the image for that phase is built.

---

## 1. Why this change

Today the agent is a bespoke session server:

- Sessions live in an in-process `Map` with idle (5 min) and lifetime (1 h) timers. **Agent restarts lose every conversation.**
- Streaming is hand-rolled SSE (`message_update`/`text_delta`, `tool_execution_*`, `agent_end`) — the frontend parses these ad hoc.
- The model is a single `PI_MODEL` env var for the whole deployment; changing models/providers requires editing manifests and redeploying.
- The UI is desktop-first, dark-only, and accumulated UX debt.

pi-durable addresses the first three directly: durable conversation state, resumable runs, a committed-state `watch()` stream, and a provider/model registry.

---

## 2. Current architecture (as-is)

```
Svelte 5 frontend ──/api/*──► ASP.NET Core API ──HTTP/SSE──► agent (pi-agent-core 0.87.1)
   (SSE parse)                  (thin proxy)                  in-memory sessions + pi-coding-agent tools
                                                                      │
                                                          /library (RWO PVC, k3sagent01)
                                                                /data (api only: sqlite)
```

**Agent surface (current):**

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | health + session list |
| GET | `/api/sessions` | list |
| GET | `/api/books/:slug/sessions` | list for book |
| POST | `/api/sessions` | ensure/create (`{bookSlug, forceNew}`) |
| GET | `/api/sessions/:id/info` | counts + token budget |
| DELETE | `/api/sessions/:id` | dispose |
| POST | `/api/sessions/:id/abort` | dispose + return last message |
| GET | `/api/books/:slug/last-message` | retry support |
| POST | `/api/sessions/:id/prompt` | non-streaming |
| POST | `/api/sessions/:id/prompt/stream` | **SSE** |
| POST | `/api/sessions/:id/compact` | compaction |

**Backend consumers of the agent:** `ChatEndpoints`, `InlineEditEndpoints`, `LoreJobService`, `ChapterSplitService`, `KnowledgeChatEndpoints`, `AgentEndpoints`, `ChatHistoryEndpoints` (kill on clear).

**Frontend SSE contract consumed in `frontend/src/lib/api.ts`:** `message_update.assistantMessageEvent{text_delta|thinking_delta}`, `tool_execution_start/end`, `agent_end`, `edit_done`, `session_info`, `error`.

**Storage:** `library-data` (20Gi RWO, `/library`, shared by api+agent, both pinned to `k3sagent01`), `sqlite-data` (2Gi RWO, `/data`, **api only**). Agent mounts `/library` + `/skills`.

---

## 3. Target architecture (to-be)

```
Svelte 5 frontend ──/api/*──► ASP.NET Core API ──/internal/agent/*──► agent
  (snapshot+ops)               (public contract, auth at ingress)      pi-durable Harness
                                                                        ├── SQLite (node:sqlite) /data/durable.sqlite
                                                                        ├── models registry (provider config, encrypted)
                                                                        ├── registry: coding tools + story/KB extensions
                                                                        └── NodeExecutionEnv cwd=/library/{slug}
                                                                              │
                                                                        /library (RWO PVC)
```

- **Agent owns** durable conversations, model registry, provider secrets (encrypted), tools, system-prompt extensions.
- **Backend owns** the public API, validation/auth, and the book library metadata; it proxies the durable stream and settings.
- **Frontend owns** a client-side transcript store that applies snapshot+ops and renders `pi.live` (partials, tool output).

---

## 4. Workstream A — pi-durable migration

### A1. Dependencies

| Package | From | To | Notes |
|---|---|---|---|
| `@earendil-works/pi-durable` | — | `1.6.0` (exact) | experimental; **pin exact**, no `^` |
| `@earendil-works/chord` | — | `1.6.0` (exact) | peer of pi-durable |
| `@earendil-works/pi-ai` | `^0.87.1` | `1.6.0` (exact) | pi-durable pins it exactly |
| `@earendil-works/pi-agent-core` | `^0.87.1` | **remove** | replaced by pi-durable Harness |
| `@earendil-works/pi-coding-agent` | `^0.87.1` | **remove** | replaced by pi-durable tools |
| `@earendil-works/pi-schema` | — | `1.6.0` | custom tool schemas |

- Engine: **Node ≥ 22.19.0** (pi-durable). `node:22-alpine` is fine; pin `node:22.19-alpine` in the Dockerfile.
- `agent/Dockerfile` must also `COPY package-lock.json` and use `npm ci` (currently only `package.json` + `npm install` — non-reproducible).
- Exact pins are deliberate: the README says the API changes without notice. Upgrades become explicit, tested PRs.

### A2. Storage

- `openNodeSqliteStorage("/data/durable.sqlite")` from `@earendil-works/pi-durable/storage/sqlite/node`.
- **New PVC `agent-data`** (2–5Gi, `local-path`, RWO) mounted at `/data` **only on the agent**. `local-path` + RWO is safe because api and agent are both pinned to `k3sagent01`.
  - Alternative: reuse `library-data` with `/library/.agent/durable.sqlite` (no new PVC, but mixes state with content; avoid).
- SQLite uses `node:sqlite` — no native module build, works on Alpine.
- Backup: nightly `sqlite3 .backup` sidecar or a Hangfire job on the API that copies the file over the shared volume. Define RPO/RTO in the rollout phase.
- **Book → conversation mapping:** a durable `app.sessions` document (`defineDocument`) holding `{ [bookSlug]: conversationId }`, written atomically with conversation creation. The backend never needs to scan. *(Verify `defineDocument`/`createConversation` signatures in the Phase 0 spike — README shows `createConversation()` but not its full option set.)*

### A3. Harness, registry, extensions, environment

```ts
const context = BACKGROUND_CONTEXT;
const models = createModels();                 // providers installed from provider config (Workstream B)
const registry = createRegistry();
registry.install(createCodingTools({ images: await createNodePhotonImages() })); // read/write/edit/bash
registry.install(StoryExtension);              // system-prompt sections (A5)
registry.install(SharedKnowledgeExtension);    // KB prompt + KB write rules
registry.install(defineExtension({ name: "web", tools: [webSearchTool, webFetchTool] }));

const harness = await Harness.open(await openNodeSqliteStorage(DB_PATH), {
  models,
  registry,
  env: ({ cwd }) => new NodeExecutionEnv({ cwd: cwd ?? "/library" }),
  settings: { /* stream timeouts, retry, compaction, progress */ },
}, context);
harness.resume();
```

- **Tools:** built-in `read`/`write`/`edit`/`bash` replace `pi-coding-agent.createCodingTools`. `web_search`/`web_fetch` are rewritten with `defineTool` + `@earendil-works/pi-schema` (same SearXNG behavior).
- **Environment:** one `NodeExecutionEnv` per call, `cwd = /library/{slug}` for stories, `/library` for `__shared__`. This preserves the current chapter/wiki file workflow (`chapters/ch-*.md`, `*.scratch.md`, `wiki/*`).
- **`*.scratch.md` diff flow:** unchanged at the file level; the backend still scans for scratch files. Under pi-durable the tool result is a durable `pi.tool-result` entry, so the backend can also detect writes from the stream instead of polling the directory.
- **Skills:** the current `/skills` ConfigMap is only referenced by the agent image/container; `createCodingTools` does not load it. Decide in the spike whether to port lore-extraction to an extension section or keep it as a file read.

### A4. Per-conversation agent

```ts
await conversation.configure({
  model: { provider: cfg.provider, modelId: cfg.modelId },
  thinkingLevel: cfg.thinkingLevel,
  tools: [...],                 // coding + web + app tools
  instructions: storyInstructions,
  cwd: `/library/${slug}`,
  maxTurns: 50,
}, context);
```

- Model resolution is **global** (owner scope): the active provider's default model. No per-book override now; the schema leaves room for it later.
- `maxTurns` replaces the implicit infinite tool loop; pick a sane bound (e.g. 50).
- `thinkingLevel` becomes a settings value (today hard-coded `"off"`).

### A5. System prompt migration

The current monolithic `SYSTEM_PROMPT` (~200 lines) becomes ordered **extension sections** so pi-durable can diff/replay them and keep provider prompt caches warm:

| Section | Content (from current prompt) |
|---|---|
| `role` | "professional fiction writing tool", file access |
| `tools` | file/wiki/KB usage rules, write-full-chapter rule, immutability of `book*.md` |
| `craft.style` | prose style, eliminate/apply lists, tone |
| `craft.slop` | banned words/phrases, hedging, em dash, rule of three, meta-writing |
| `craft.simulation` | scene checklist, narrative core, character architecture, NPC coherence, dialogue, anti-resolution, genre calibration, tone table, prose economy, POV |
| `craft.explicit` | explicit-scene craft |
| `freedom` | creative-freedom block (keep verbatim) |
| `instructions` | per-conversation author intent (the "proxy protocol"/canon-verification rules) |

- `KB_SYSTEM_PROMPT` becomes `SharedKnowledgeExtension` with its own sections, selected only for `__shared__`.
- Sections must be **static** (no timestamps/randomness) to preserve prompt caching.
- Acceptance: a golden-text test asserts the rendered prompt for a story conversation is byte-identical to the current prompt (modulo section wrappers). This guards against silent craft-rule loss.

### A6. Agent HTTP API (new contract)

The agent becomes conversation-oriented and **stream-first**:

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | liveness + harness/DB status |
| GET | `/api/conversations` | list (slug, id, title, updated) |
| POST | `/api/conversations` | ensure conversation for `{slug, mode}` |
| GET | `/api/conversations/:id` | metadata + agent config |
| DELETE | `/api/conversations/:id` | reset/delete |
| GET | `/api/conversations/:id/stream?from=<cursor>` | **SSE: snapshot + ops** |
| POST | `/api/conversations/:id/input` | `{content, whenBusy, whenIdle, requestId, entry?}` |
| POST | `/api/conversations/:id/abort` | abort current run |
| POST | `/api/conversations/:id/configure` | model/thinking/tools/instructions/cwd |
| POST | `/api/conversations/:id/compact` | manual compaction |
| GET | `/api/conversations/:id/usage` | tokens/cost |
| GET | `/api/models` | catalog for settings UI |
| GET/PUT | `/api/settings/llm` | provider config (masked) |
| POST | `/api/settings/llm/test` | test connection |

**Wire format** (SSE `data:` frames):

```jsonc
// 1. first frame after attach
{ "v": 1, "kind": "snapshot",
  "value": {
    "id": "…", "entries": [ /* newest 50 */ ], "before": "…",
    "docs": { "pi.live": {...}, "pi.inbox": {...}, "pi.usage": {...},
              "pi.agent": {...}, "pi.provider": {...} }
  } }

// 2. subsequent frames (folded Chord ops)
{ "v": 1, "kind": "ops", "ops": [ /* ["a", index, entry], ["s", path, value], ["r", value] */ ] }

// 3. terminal
{ "v": 1, "kind": "end", "reason": "stopped|ended|failed" }
```

- `pi.live` carries the streamed partial, tool calls with output/details, and `runFailure` — this replaces `message_update`/`tool_execution_*`.
- `pi.usage` replaces the custom token counter.
- **Reconnect:** the frontend keeps a cursor from the last applied op; on drop it re-attaches with `from`. Server-side, use `watch(context, { from })` / `ConversationTranscript.resume`.
- The backend proxies this stream **verbatim** (`text/event-stream`), adding no transformation; it only injects book context as an `input.entry` (the current `contextHint` logic moves into the backend as a structured `entry` or stays in the input `content`).
- `edit_done` disappears: the backend derives scratch-file edits from `pi.tool-result`/`pi.live` entries (or keeps the directory scan as a safety net).

**Mapping old → new (backend call sites):**

| Old | New |
|---|---|
| `EnsureSessionAsync` | `POST /api/conversations` |
| `CreateNewSessionAsync` | `DELETE` + `POST /api/conversations` |
| `SendPromptAsync` | `POST /input` + wait for settlement (or read the transcript) |
| `StreamPromptAsync` | `GET /stream` (attach) + `POST /input` |
| `AbortSessionAsync` | `POST /abort` |
| `GetSessionInfoAsync` | `GET /api/conversations/:id` + `/usage` |
| `CompactSessionAsync` | `POST /compact` |
| `ListSessionsAsync` | `GET /api/conversations` |
| `GetLastUserMessageAsync` | read transcript tail (or `GET /:id` summary) |

### A7. Data migration

- Existing `ChatMessages` remain the historical record; **new** conversations are durable transcripts.
- Backfill (optional, one-shot): for each book/session, append `pi.user`/`pi.assistant` entries in order, preserving `CreatedAt` in entry `data`. Idempotent by `requestId = "backfill:{messageId}"`.
- Old in-memory session IDs are not recoverable; they are replaced by conversation IDs.
- `SessionCleanupService` (nightly) becomes a durable-storage retention job (or is dropped; pi-durable already compacts).

### A8. Risks (Workstream A)

| Risk | Mitigation |
|---|---|
| pi-durable API churn (experimental) | exact pins; Phase 0 spike; contract tests around our adapter |
| pi-ai 0.87 → 1.6 breaking changes | migrate to `createModels()` + provider factories; `/compat` still exists as a fallback |
| SQLite write contention / local-path disk | single agent replica; WAL; backup job; monitor `durable.sqlite-wal` |
| Resume semantics differ from "abort" | map `abort` to pi-durable abort; verify queued-input behavior |
| Prompt regression | golden-prompt test in A5 |
| Streaming UX regressions | ops applier + reconnect tests; keep a `?legacy=1` debug stream during Phase 1 **only in dev** |

---

## 5. Workstream B — LLM provider configuration

### B1. Ownership and security model

**Recommendation: the agent owns the config** (it needs plaintext keys at call time), the backend proxies the settings API, and the public API never returns a key.

- Config file: `/data/llm-config.json` (atomic write: temp + rename) **or** a row in the durable SQLite DB. JSON is simpler to inspect/rotate; the DB is transactional. Pick JSON + atomic rename.
- Encryption at rest: **AES-256-GCM**, key from `CONFIG_ENCRYPTION_KEY` (32-byte base64) in a k8s secret. Ciphertext stores `{v, iv, tag, ct}`.
- Responses always mask (`sk-…4f2a`); never log keys; redact `Authorization` in errors.
- Env fallback: if no provider is configured, fall back to `DEEPSEEK_API_KEY`/`PI_MODEL` so an upgrade never bricks the deployment. On first run, optionally seed a provider from the env key.

### B2. Data model

```jsonc
{
  "version": 1,
  "defaultProviderId": "deepseek",
  "defaultModelId": "deepseek-flash",
  "thinkingLevel": "off",
  "providers": [
    {
      "id": "deepseek",            // stable slug
      "name": "DeepSeek",          // display
      "kind": "deepseek",          // pi-ai provider factory key
      "baseUrl": null,             // null = factory default
      "apiKey": { "v": 1, "iv": "…", "tag": "…", "ct": "…" },
      "keyLast4": "8f2a",
      "enabled": true,
      "createdAt": "…", "updatedAt": "…"
    }
  ]
}
```

- `kind` maps to a curated factory set: `openai`, `anthropic`, `google`, `deepseek`, `openrouter`, `mistral`, `groq`, `xai`, `azure`, `custom` (OpenAI-compatible base URL + key).
- Model list per provider comes from `provider.getModels()` / the generated catalog, plus a free-text model id for custom endpoints.
- Validation: URL shape, non-empty key, unique id, one default.

### B3. Provider wiring

```ts
// build the registry from config
for (const p of enabledProviders) {
  const factory = FACTORIES[p.kind];         // e.g. deepseekProvider, openaiProvider, …
  const provider = p.baseUrl || p.kind === "custom"
    ? createProvider({ id: p.id, baseUrl: p.baseUrl, auth: { apiKey: decrypt(p.apiKey) }, models: [...] })
    : factory({ apiKey: decrypt(p.apiKey) }); // verify factory option name in Phase 0
  models.setProvider(provider);
}
```

> Phase 0 must confirm how pi-ai 1.6.0 provider factories accept an explicit key (`openaiProvider()` is declared with no args in the d.ts; the key may come from `createProvider({ auth })` or `models` credential store). The plan assumes explicit `auth`; if factories only read env, use `createProvider` with `auth` (documented as required) or set per-request credentials.

- Config changes rebuild/replace providers via `models.setProvider` + `registry` reload; conversations pick the new model on their next `configure()`/request.

### B4. HTTP API (public, backend)

| Method | Path | Body / result |
|---|---|---|
| GET | `/api/settings/llm` | providers (masked) + default + thinking |
| POST | `/api/settings/llm/providers` | `{name, kind, baseUrl, apiKey}` → masked provider |
| PUT | `/api/settings/llm/providers/:id` | update (apiKey optional = unchanged) |
| DELETE | `/api/settings/llm/providers/:id` | delete (409 if default) |
| POST | `/api/settings/llm/providers/:id/test` | `{ok, latencyMs, models?, error?}` |
| GET | `/api/settings/llm/models?provider=:id` | catalog |
| PUT | `/api/settings/llm/default` | `{providerId, modelId, thinkingLevel}` |

- All go through the backend (auth/CORS/validation) → agent internal endpoints.
- `test` performs a minimal models list or a 1-token completion with a timeout; never echoes the key.

### B5. Settings UI

- New route `/settings`, section **Models & Providers**.
- Provider cards: name, kind, masked key, default badge, enabled toggle, Test/Edit/Delete.
- Add/Edit drawer: provider kind select (curated list), base URL (only for custom/override), API key (write-only, placeholder shows last4), Test button with inline result.
- Default model section: provider → model combobox, thinking level, "used by all books" note.
- Empty state: "Using server defaults (DEEPSEEK_API_KEY)" with a one-click import.
- Errors surfaced via the existing toast store.

### B6. Migration / rollout

1. Ship provider config **dormant**: UI visible, config empty, env fallback active.
2. Seed from env (button or environment flag).
3. Flip `defaultProviderId` and remove `PI_MODEL` from the agent deployment once verified.
4. Keep `CONFIG_ENCRYPTION_KEY` in a secret; document rotation (re-encrypt on change).

---

## 6. Workstream C — UI/UX revamp (light minimal writing studio)

### C1. Principles

- **The manuscript is the interface.** Chrome recedes; paper is the brightest surface.
- **Quiet, literary, precise.** Serif for prose/display, humanist sans for UI; one accent, used sparingly.
- **Density with air.** Generous line-height in the editor; compact tool chrome.
- **Progressive disclosure.** Panels collapse; advanced settings live in Settings.
- **Keyboard-first.** Every primary action has a shortcut; focus is always visible.

### C2. Design tokens (single source: `frontend/src/lib/design/tokens.css`)

| Token | Value |
|---|---|
| `--paper` | `#FAF9F7` |
| `--paper-raised` | `#FFFFFF` |
| `--paper-sunken` | `#F2F0EC` |
| `--ink` | `#1C1B19` |
| `--ink-2` | `#4A4844` |
| `--ink-3` | `#7A766F` |
| `--rule` | `#E4E0D9` |
| `--accent` | `#3B5B7A` (muted ink-blue) |
| `--accent-soft` | `#E8EEF4` |
| `--danger` | `#9B3B34` |
| `--warn` | `#8A6A1F` |
| `--ok` | `#3F6B4F` |
| `--font-display` | `"Source Serif 4", "Charter", Georgia, serif` |
| `--font-ui` | `"Inter", system-ui, sans-serif` |
| `--font-mono` | `"JetBrains Mono", ui-monospace` |
| `--radius` | `6px` / `10px` |
| `--space` | `4/8/12/16/24/32/48/64` |
| `--shadow` | `0 1px 2px rgb(0 0 0 / .06), 0 8px 24px rgb(0 0 0 / .06)` |

- Optional `prefers-color-scheme: dark` maps the same semantic tokens (out of primary scope; ship if cheap).
- No hard-coded colors in components — tokens only (lint rule or grep gate).

### C3. Information architecture

```
Library (/)                      → book grid/list, create, upload, status
Book (/books/[slug])             → AppShell: Chapters │ Manuscript │ Assistant
  ├─ Lore/Wiki tab (panel)
  └─ Inline edit + diff overlay
Knowledge (/knowledge)           → shared KB browser + research assistant
Settings (/settings)             → Models & Providers, Appearance, Data
```

### C4. App shell and responsive layout

| Width | Layout |
|---|---|
| ≥ 1280 | 3 columns: chapters rail (240) + editor (fluid) + assistant (380), both rails collapsible |
| 768–1279 | 2 columns: editor + one rail; the other becomes a drawer |
| < 768 | 1 column: editor; bottom tab bar (Chapters / Assistant / Lore); full-screen sheets |

- Implement with CSS grid + container queries; no JS layout thrash.
- Editor: max measure ~68ch, `font-display`, 1.7 line-height, comfortable paragraph spacing.
- Command palette (`⌘K`): jump to chapter/book, run actions (new chapter, compress, export).
- Keyboard: `⌘S` save, `⌘Enter` send, `Esc` close overlays, `⌘K` palette.

### C5. Screen specs (textual wireframes)

**Library**
```
┌ Continue Story                         search  sort  + New ┐
│ [cover] Title            Author      words  status  ⋯     │
│ [cover] Title            Author      words  status  ⋯     │
└───────────────────────────────────────────────────────────┘
```
- Status chips (converting / lore-ready / error) with retry; empty state with upload dropzone; sort persistence.

**Book workspace**
```
┌ ⌘K  Title ▾        Saved ✓         Lore  Export  ⋯        ┐
│ Chapters      │  Chapter 12 — The Long Night        │ Chat │
│ 01 …          │  … prose, 68ch measure …           │ …    │
│ 02 …          │                                    │      │
│ + add         │                                    │ input│
└────────────────────────────────────────────────────────────┘
```
- Unsaved indicator + flush on switch; diff overlay on `*.scratch.md`; inline-edit menu anchored to selection.

**Assistant panel**
- Streaming text with tool cards (collapsible: name, args, output tail), thinking disclosure, stop/retry, session badge, usage.
- Tool cards driven by `pi.live` tool entries (not ad hoc events).

**Settings → Models & Providers**
- Provider cards + default model picker + Test connection; key write-only.

### C6. Component inventory

**New:** `AppShell`, `Rail`, `Sheet`, `CommandPalette`, `ProviderCard`, `ProviderDrawer`, `ModelPicker`, `SettingsNav`, `EmptyState`, `ToolCard`, `StreamStatus`, `Skeleton`, `IconButton`, `Field`.
**Rework:** `BookList`, `BookEditor`, `ChapterSidebar`, `ChatPanel`, `LorePanel`, `KnowledgePanel`, `DiffOverlay`, `InlineEditMenu`, `UploadZone`, `Toasts`.
**Delete/replace:** `tool-labels.ts` (labels move into tool metadata), ad hoc SSE parser in `api.ts` (replaced by `lib/stream/ops.ts`).

### C7. UX debt to fix in the revamp (carry-over from the 2026-08-02 sweep)

- Save integrity (flush on chapter switch/lock, `beforeunload`, unsaved indicator).
- Toast coverage for every silent failure.
- Chat: retry dedup, stick-to-bottom, partial preservation, session-filtered history.
- Markdown sanitization (DOMPurify) + external link handling.
- Accessibility: focus-visible, aria-live for stream/toasts, contrast ≥ 4.5:1, keyboard traps in overlays.
- Performance: no layout shift on stream; render debounce; virtualize very long chapters (later).

### C8. Acceptance

- `npm run check` clean; Lighthouse a11y ≥ 95 on Library/Book/Settings.
- No hard-coded colors; tokens only.
- 3 breakpoints verified; all primary actions keyboard-reachable.
- Visual regression snapshots for the 3 main screens (Playwright or Storybook if added).

---

## 7. Phasing

| Phase | Deliverable | Acceptance |
|---|---|---|
| **0. Spike** (1–2 d) | Throwaway harness: pi-durable + SQLite + `watch` ops over SSE; verify `createConversation`, `defineDocument`, provider `auth` key injection, Node 22.19 on Alpine | Demo stream + resume after kill |
| **1. Agent foundation** | Harness, storage + `agent-data` PVC, tools, extensions, prompt sections, conversation API, ops stream; old session API removed | Contract tests; kill -9 resume; golden prompt |
| **2. Backend + transport** | New `IAgentService`, `/api/chat` → durable stream, all call sites migrated, reconnection | Integration tests; no `/api/sessions` references |
| **3. Provider config** | Encrypted config, provider/model catalog, test endpoint, env fallback + seed | Add/test/select provider end-to-end; key never returned |
| **4. UX revamp** | Tokens, AppShell, responsive, screens, component rework, Settings UI | svelte-check, a11y, visual snapshots |
| **5. Rollout** | Migration/backfill, backup, docs, remove `PI_MODEL`, cleanup | Fresh deploy + restore drill |

**Sequencing rationale:** 1→2 unblocks the durable transport; 3 is small and independent once the model registry exists; 4 is the largest surface and benefits from the stable `pi.live` shape.

---

## 8. Testing strategy

- **Agent:** contract tests over `MemoryStorage` and `openNodeSqliteStorage`; resume (SIGKILL mid-run); tool tests (read/write/edit/bash/web); ops-stream golden tests; prompt golden tests; provider-config encrypt/decrypt/mask/test.
- **Backend:** `WebApplicationFactory` integration tests for the new chat stream, settings CRUD, and validation; ensure no key leakage in responses/logs.
- **Frontend:** `svelte-check`; unit tests for the ops applier and transcript merge; Playwright e2e for library → book → chat → accept diff, and settings → add provider → test.
- **Stack:** docker-compose smoke (`/api/health`, a real prompt, a scratch-file accept).
- **Regression gates:** `tsc --noEmit`, `dotnet build && dotnet test`, `npm run check && npm run build`.

---

## 9. Deployment & rollout

1. **New PVC** `agent-data` mounted at `/data` on the agent (same node pin).
2. **Secrets:** `CONFIG_ENCRYPTION_KEY` (new); keep `agent-keys` for env fallback during transition.
3. **Dockerfile:** `node:22.19-alpine`, `COPY package*.json`, `npm ci`.
4. **Image/CI:** unchanged GHCR workflow; agent tag `:latest` + `:<sha>`.
5. **ArgoCD:** auto-sync is paused (see the header procedure). `master` keeps receiving commits; rollout happens only via manual phase-gate syncs. Restore `automated: {prune, selfHeal}` after cutover. Rollback = previous image tag (in-memory session state from the old model is not recoverable by design).
6. **Backup:** nightly SQLite `.backup` into `library-data`; document restore.
7. **Cutover:** verify `/health`, stream a prompt, kill the pod mid-run and confirm resume, then remove `PI_MODEL`.

---

## 10. Risks & mitigations (top-level)

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| pi-durable API breaks on upgrade | High over time | High | exact pins; adapter layer; spike before each bump |
| pi-ai 1.6 provider auth mismatch | Medium | Medium | Phase 0 verifies key injection; fallback to `createProvider({auth})` |
| Durable DB corruption/disk full | Low | High | WAL, backup job, PVC monitoring, `integrity_check` |
| Auto-deploy exposes half-migrated transport | High | High | pause ArgoCD auto-sync; phase gates |
| Prompt/craft regression | Medium | High | golden-prompt test; diff review |
| UX revamp churn | Medium | Medium | tokens + component inventory; snapshot tests |
| Key leakage | Low | Critical | encrypt at rest, mask, no-log, redaction tests |

---

## 11. Open questions (resolve before Phase 0 exit)

1. **pi-durable conversation creation:** exact `createConversation()` signature and how to persist the book→conversation map atomically (`defineDocument` API not in the npm README excerpt).
2. **Provider auth:** do 1.6.0 factories accept `{apiKey}` or must we use `createProvider({auth})`? Confirm for deepseek/openai/anthropic/openrouter/custom.
3. **Model catalog for the UI:** `models.getModels()` requires providers to be registered first — confirm the catalog is available before a key is entered (static `getModels()` on the factory).
4. **`pi.live` shape stability:** confirm the run/tool/partial fields we map to UI.
5. **Skills:** keep `/skills` lore-extraction as a section or a tool?
6. **Auth:** Settings exposes provider keys; confirm Authelia/ingress is the only auth boundary and whether an admin-only gate is needed.
7. **Backfill:** do we import existing chat history, or treat it as read-only legacy?

---

## 12. Explicit non-goals (this scope)

- Per-book model overrides.
- Multi-user/tenancy, RBAC, i18n.
- Mobile native app.
- Swapping the ASP.NET backend or Svelte frontend.
- Real-time collaborative editing.
