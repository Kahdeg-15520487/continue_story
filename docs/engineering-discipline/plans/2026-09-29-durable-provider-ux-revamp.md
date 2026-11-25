# Durable Harness + Provider Config + UX Revamp — Design & Migration Plan

- **Date:** 2026-09-29
- **Status:** Revision 2 (2026-09-29) — built-in-first pass
- **Revision 2 changes:** prompt as registry `section`s (not stored `instructions`), `reset`/`fork` (not custom replace), built-in `ConversationTranscript` (not a hand-written ops applier), the documented `models.json` schema, durable tasks for lore/chapter-split, and `beforeTool`/`afterTool` invariants.
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

### 3.1 Built-in-first principle

Our code should be **transport, content, and adapters** — nothing pi-durable
already owns.

- **No custom state model.** Conversations, entries, documents, tasks, inbox,
  usage and lifecycle come from the harness.
- **No custom prompt plumbing.** Prompt text lives in extension **sections**;
  `instructions` is reserved for per-book author directives.
- **No custom lifecycle.** “Clear chat” is `reset()`, “branch” is `fork()`; there
  is no delete/replace.
- **No custom transcript merge.** The client uses the built-in
  `ConversationTranscript` (`merge`, `resume`, `loadEarlier`, `fillGap`).
- **No custom model runtime.** `createModels`, `CredentialStore`, `ModelsStore`,
  `createProvider`, and the documented `models.json` schema.
- **No custom scheduling.** Long agent work is `defineTask`, not ad-hoc HTTP +
  external retries.

| Concern | Built-in we use | Custom that remains |
|---|---|---|
| Durability / resume | `Harness`, SQLite storage | — |
| Transcript merge / reconnect | `ConversationTranscript` | thin SSE transport |
| Prompt | `section` + `defineExtension` | prompt content only |
| Tool invariants | `beforeTool` / `afterTool` hooks | the rules themselves |
| Lifecycle | `reset` / `fork` / `abort` / `compact` | — |
| Long jobs | `defineTask` / `waitForTask` | task definitions + task endpoints |
| Models / auth | `createModels`, `CredentialStore`, `ModelsStore`, `identity` | encrypted secret file + UI |
| Tools | `CodingTools`, `defineTool` | SearXNG web tools only |
| Book context | conversation document + section | the context content |
| Book→conversation index | `defineDoc` | the tiny doc definition |
| Usage / cost | `UsageDoc` | — |

- **Agent owns** the harness, registry, provider config and secrets.
- **Backend owns** the public API, validation/auth, and book metadata; it proxies the stream and settings.
- **Frontend owns** rendering; transcript state comes from `ConversationTranscript`.

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
const models = createModels({
  credentials: encryptedCredentialStore,   // CredentialStore over an AES-GCM file
  modelsStore: new InMemoryModelsStore(),  // catalog overlays
  identity: { attribution: { name: "continue-story" } },
});

const registry = createRegistry();
registry.install(CodingTools);
registry.install(WebTools);                 // defineTool + pi-schema
registry.install(StoryExtension);           // sections (A5)
registry.install(KnowledgeExtension);       // sections
registry.install(InvariantsExtension);      // beforeTool/afterTool hooks

const harness = await Harness.open(await openNodeSqliteStorage(DB_PATH), {
  models,
  registry,
  env: ({ cwd }) => new NodeExecutionEnv({ cwd: cwd ?? "/library" }),
  settings: liveSettings(),                 // getters, re-read at every use
}, BACKGROUND_CONTEXT);
harness.resume();
```

- **Tools:** built-in `read`/`write`/`edit`/`bash` replace `pi-coding-agent.createCodingTools`. `web_search`/`web_fetch` are rewritten with `defineTool` + `@earendil-works/pi-schema` (same SearXNG behavior).
- **Environment:** one `NodeExecutionEnv` per call, `cwd = /library/{slug}` for stories, `/library` for `__shared__`. This preserves the current chapter/wiki file workflow (`chapters/ch-*.md`, `*.scratch.md`, `wiki/*`).
- **`*.scratch.md` diff flow:** the write/edit tool's committed `pi.tool-result` entry carries the path in `details`; derive `edit_done` from that entry (no filesystem scan). Optionally an `afterTool` hook appends an `app.edit` marker entry in the same commit.
- **Invariants:** a `beforeTool` hook blocks writes outside `cwd`, edits to `book.md`/`book.org.md`, and path escapes; an `afterTool` hook surfaces scratch writes. Prompt text stops being the only enforcement.
- **Skills:** port `/skills/lore-extraction` to a `KnowledgeExtension` section so it renders through the registry; the `/skills` ConfigMap can be retired.

### A4. Per-conversation agent

```ts
await conversation.configure({
  model: { provider: cfg.provider, modelId: cfg.modelId },
  thinkingLevel: cfg.thinkingLevel,
  cwd: `/library/${slug}`,
  maxTurns: 50,
  // `instructions` is reserved for per-book author directives, not craft rules:
  // instructions: bookDirectives,
}, context);
```

- Craft/role/tool rules come from the registry's sections; `instructions` is empty by default and only carries per-book author intent.
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

### A6. Agent HTTP API (built-in vocabulary)

The transport stays custom (pi-durable is a library, not a server), but it exposes
the harness's own verbs instead of re-inventing them.

| Method | Path | Built-in it maps to |
|---|---|---|
| GET | `/health` | `harness.inspect()` + DB path |
| GET | `/api/conversations` | `tx.scanConversations` (+ `app.sessions` index) |
| POST | `/api/conversations` | `createConversation` + `app.sessions` doc |
| GET | `/api/conversations/:id` | `conversation.agent()` + `UsageDoc` |
| POST | `/api/conversations/:id/input` | `conversation.submit()` |
| POST | `/api/conversations/:id/abort` | `conversation.abort()` |
| POST | `/api/conversations/:id/reset` | `conversation.reset(handoff?)` |
| POST | `/api/conversations/:id/fork` | `conversation.fork(at, options)` |
| POST | `/api/conversations/:id/compact` | `conversation.compact()` |
| POST | `/api/conversations/:id/configure` | `conversation.configure()` |
| GET | `/api/conversations/:id/stream` | `conversation.watch()` |
| GET | `/api/conversations/:id/entries` | `conversation.entries()` (`loadEarlier`/`fillGap`) |
| GET | `/api/conversations/:id/usage` | `UsageDoc` |
| GET | `/api/tasks`, `/api/tasks/:id` | `harness.inspect()` / `getTask()` / `waitForTask()` |
| POST | `/api/tasks` | `tx.createTask()` (`ChapterSplitTask`, `LoreTask`) |
| GET | `/api/models` | `models.getProviders()` + `getModels()` |
| GET/PUT | `/api/settings/llm` | config file + `CredentialStore` |
| POST | `/api/settings/llm/test` | provider resolution |

**Lifecycle:** “clear chat” is `reset()` (history kept, new context); “branch”
is `fork()`; there is **no custom delete/replace**. Deleting a book unmaps the
conversation; the transcript remains in durable storage.

**Wire format:** forward Chord's `WatchCall` shape rather than a bespoke event
model:

```jsonc
{ "v": 1, "kind": "value", "value": { /* ConversationView */ }, "ops": [ /* Chord Op[] */ ] }
{ "v": 1, "kind": "end", "reason": "stopped|ended|failed" }
```

The client owns the merge with the **built-in** `ConversationTranscript`:

```ts
const transcript = new ConversationTranscript(
  (query, limit, ctx) => proxy.entries(query, limit, ctx),
);
transcript.merge(view);              // items, gaps, “load earlier” markers
conv.watch(ctx, transcript.resume);  // reconnect where it left off
```

`ConversationTranscript` is browser-safe (Node imports live only in
`images/node` and `env/node-watch`, outside the root graph). Verify bundling in
Phase 2; server-side merging is the fallback.

**Edits:** derive `edit_done` from the committed `pi.tool-result` entry for the
`write`/`edit` tool (its `details` carries the path), not from a filesystem
scan. Optionally an `afterTool` hook appends an `app.edit` marker entry in the
same commit.

**Book context:** a small `app.context` conversation document rendered by a
section, instead of prepending dynamic text to every user message.

**Mapping old → new:**

| Old | New |
|---|---|
| `EnsureSessionAsync` | `POST /api/conversations` |
| `CreateNewSessionAsync` | `POST /:id/reset` |
| `StreamPromptAsync` | `GET /stream` (attach) + `POST /input` |
| `AbortSessionAsync` | `POST /abort` |
| `GetSessionInfoAsync` | `GET /api/conversations/:id` + `/usage` |
| `CompactSessionAsync` | `POST /compact` |
| `ListSessionsAsync` | `GET /api/conversations` |
| `GetLastUserMessageAsync` | transcript tail |
| `SendPromptAsync` (lore/split) | `POST /api/tasks` + `GET /api/tasks/:id` |

### A6.1 Durable tasks — replace agent-side retries

`LoreJobService` and `ChapterSplitService` call the non-streaming `/prompt`
endpoint and rely on Hangfire for retries. The agent-side work is exactly what
built-in tasks are for:

```ts
const ChapterSplitTask = defineTask({ name: "app.chapter-split", version: 1, /* phases */ });
const LoreTask = defineTask({ name: "app.lore", version: 1, /* phases */ });

// start (HTTP): POST /api/tasks { kind: "lore", slug }
const id = await harness.commit(
  (tx) => tx.createTask(LoreTask, { slug }, { ownership: { kind: "ownerless" } }),
  ctx,
);
// status (HTTP): GET /api/tasks/:id → harness.getTask / waitForTask
```

- Phases checkpoint after every model/tool call; a crash resumes.
- `settings.restartLimit` bounds repeated crashes.
- Hangfire stays for file conversion; it waits on the durable task instead of
  holding a long HTTP call.

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
| Streaming UX regressions | `ConversationTranscript` adapter tests + reconnect tests; keep a `?legacy=1` debug stream during Phase 2 **only in dev** |
| `ConversationTranscript` does not bundle in Vite | verify in the Phase 2 spike; fallback = server-side merge (send `TranscriptView.items` + `pi.live`) |
| Hooks block legitimate writes | ship hooks warn-only for one phase, then enforce; log blocked paths |

---

## 5. Workstream B — LLM provider configuration

### B1. Use the built-in model plumbing

The custom surface should be **the settings UI and encrypted secret storage** —
the model runtime is pi-ai's.

- `createModels({ credentials, modelsStore, identity })` — built-in registries.
  - `credentials`: a `CredentialStore` over an AES-256-GCM encrypted JSON file
    (`CONFIG_ENCRYPTION_KEY` from a k8s secret).
  - `modelsStore`: `InMemoryModelsStore` initially; a small file adapter for
    catalog overlays if persistence is wanted.
  - `identity`: `{ attribution: { name: "continue-story", url: … } }`.
- Config uses the **documented `models.json` schema** (`$schema`,
  `providers.{id}.{baseUrl, api, apiKey, headers, models, modelOverrides}`), so
  it is compatible with the pi CLI and the published schema.
- `apiKey` accepts a literal, `$ENV`/`${ENV}`, or `!command` (built-in
  resolution), so secret-managed deployments need not store keys in the app.
- Providers without a built-in factory use `createProvider` (built-in) with
  `auth`, `models`, `api`.
- `pi-ai` does **not** parse `models.json` itself (that is pi-coding-agent's
  ModelManager). We implement a ~50-line loader for the documented schema and do
  **not** reintroduce pi-coding-agent.
- Responses always mask keys; never log them; redact `Authorization`.
- Env fallback: if no provider is configured, fall back to `DEEPSEEK_API_KEY`/
  `PI_MODEL` so an upgrade never bricks the deployment.

### B2. Data model (models.json-compatible)

```jsonc
{
  "$schema": "https://pi.dev/schemas/models.schema.json",
  "defaultProvider": "deepseek",
  "defaultModel": "deepseek-flash",
  "thinkingLevel": "off",
  "providers": {
    "deepseek": {
      "api": "openai-completions",
      "apiKey": "!continue-story-secret deepseek",   // or "$DEEPSEEK_API_KEY"
      "models": [{ "id": "deepseek-flash" }]
    },
    "local": {
      "baseUrl": "http://ollama:11434/v1",
      "api": "openai-completions",
      "apiKey": "ollama",
      "models": [{ "id": "qwen2.5-coder:7b" }]
    }
  },
  "modelOverrides": {}
}
```

- App-only metadata (`keyLast4`, `createdAt`, `enabled`) lives beside the
  schema-compatible fields; the loader ignores unknown keys.
- Secrets are either `$ENV`/`!command` refs or ciphertext in the encrypted
  `CredentialStore`; never inline plaintext in the config file.
- Validation: URL shape, non-empty key, unique provider id, one default.

### B3. Provider wiring (built-in)

```ts
const config = loadModelsJson(CONFIG_PATH);            // documented schema
const models = createModels({
  credentials: encryptedCredentialStore,               // CredentialStore
  modelsStore: new InMemoryModelsStore(),
  identity: { attribution: { name: "continue-story" } },
});

for (const [id, p] of Object.entries(config.providers)) {
  const factory = BUILTIN_FACTORIES[id];               // deepseekProvider, openaiProvider, …
  models.setProvider(
    factory && !p.baseUrl
      ? factory()                                      // key comes from CredentialStore
      : createProvider({                               // built-in custom endpoint
          id,
          name: p.name ?? id,
          baseUrl: p.baseUrl,
          auth: { apiKey: resolveAuth(p) },
          models: p.models ?? [],
          api: apiFor(p.api),
        }),
  );
}
```

- Config changes call `models.setProvider()` again (the registry is live); no
  restart needed. Conversations keep their stored `pi.agent` model until
  `configure()` is called — an explicit policy decision (see §11).

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
- **Thin client state.** Transcript merge, gaps, and reconnect come from the built-in `ConversationTranscript`; the client renders `items` and `pi.live`, it does not re-implement the harness.

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
**Delete/replace:** `tool-labels.ts` (labels move into tool metadata), ad hoc SSE parser in `api.ts`, and any hand-written ops applier — replaced by the built-in `ConversationTranscript` plus `pi.live` rendering.

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
| **1. Agent foundation** | Harness, storage + `agent-data` PVC, tools, extensions, conversation API, ops stream; old session API removed | Contract tests; kill -9 resume |
| **1.5 Built-in alignment** | Prompt → registry `sections`; `beforeTool`/`afterTool` invariants; `reset`/`fork`; edits from committed tool results; `ConversationTranscript` + reconnect; live settings getters; `identity` | Golden prompt; hook tests; reconnect + reset/fork tests |
| **2. Backend + transport** | New `IAgentService`, `/api/chat` → durable stream, all call sites migrated, reconnection | Integration tests; no `/api/sessions` references |
| **3. Provider config** | Encrypted config, provider/model catalog, test endpoint, env fallback + seed | Add/test/select provider end-to-end; key never returned |
| **4. UX revamp** | Tokens, AppShell, responsive, screens, component rework, Settings UI | svelte-check, a11y, visual snapshots |
| **5. Rollout** | Migration/backfill, backup, docs, remove `PI_MODEL`, cleanup | Fresh deploy + restore drill |

**Sequencing rationale:** 1→1.5 removes the remaining custom re-implementations before the backend binds to them; 2 unblocks the durable transport; 3 is small and independent once the model registry exists; 4 is the largest surface and benefits from the stable `pi.live` shape.

### Phase 1.5 — align the committed Phase 1 code

The committed Phase 1 (`6f18f47`) still contains the pre-revision choices. Before
Phase 2:

1. Move `STORY_INSTRUCTIONS`/`KB_INSTRUCTIONS` into `StoryExtension`/
   `KnowledgeExtension` **sections**; drop `instructions` from
   `configureConversation` (reserve it for per-book author directives).
2. Replace `replaceConversation` with `reset()` and add `fork()`.
3. Add `InvariantsExtension` (`beforeTool`: path scope + `book*.md`;
   `afterTool`: scratch marker).
4. Convert `settings` to getters backed by config; set `identity`.
5. List via `tx.scanConversations`; keep `app.sessions` only as the slug index.
6. Add the `ConversationTranscript` + reconnect path (the client lands in
   Phase 2).

---

## 8. Testing strategy

- **Agent:** contract tests over `MemoryStorage` and `openNodeSqliteStorage`; resume (SIGKILL mid-run); tool tests (read/write/edit/bash/web); hook tests (blocked paths, `book.md` immutability, scratch markers); task tests (lore/split resume); ops-stream golden tests; prompt-section golden tests; provider-config encrypt/decrypt/mask/test.
- **Backend:** `WebApplicationFactory` integration tests for the new chat stream, settings CRUD, and validation; ensure no key leakage in responses/logs.
- **Frontend:** `svelte-check`; tests for the thin `ConversationTranscript` adapter (`merge`/`resume`/gaps) and `pi.live` rendering; Playwright e2e for library → book → chat → accept diff, and settings → add provider → test.
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

## 11. Open questions

**Resolved by the Phase 0 spike** (details in `2026-09-29-durable-spike-findings.md`):

1. ~~**pi-durable conversation creation**~~ — `tx.createConversation({ ownership: { kind: "ownerless" } })` inside `harness.commit`; the book→conversation map is persisted atomically with `defineDoc({ kind: "app.sessions", version: 1, scope: "session", … })`.
2. ~~**Provider auth**~~ — built-in factories stay arg-less; inject keys via `createModels({ credentials: store })` where `CredentialStore.read()` returns `{ type: "api_key", key }`. No env var required.
3. ~~**Model catalog before a key**~~ — `models.getModels()` is the static pre-key catalog; `models.getAvailable()` is credential-gated (empty without a key).
4. ~~**`pi.live` shape**~~ — `run?`, `generation?{attempt,message,retry,deferred}`, `tools?: ToolSlot[]`, `nestedTools?`, `compactions?`, `runFailure?`. Tool slots live under `tools`, not `toolCalls`.

**Still owner decisions:**

5. **Skills:** keep `/skills` lore-extraction as a section or a tool?
6. **Auth:** Settings exposes provider keys; confirm Authelia/ingress is the only auth boundary and whether an admin-only gate is needed.
7. **Backfill:** do we import existing chat history, or treat it as read-only legacy?

**Raised by the built-in-first pass:**

8. **`ConversationTranscript` in SvelteKit/Vite** — confirm the root graph bundles without Node shims; fallback is server-side merge.
9. **`afterTool` entry writes** — can a hook append an `app.edit` marker, or does the client derive it from `pi.tool-result`?
10. **`models.json` compatibility** — adopt the documented schema (recommended) or only our stored shape?
11. **Model-change policy** — when the global default changes, reconfigure existing conversations or leave them pinned to their stored model?
12. **Hangfire retirement** — move lore/split fully onto durable tasks, or keep Hangfire as the scheduler?
13. **Book scope** — should `beforeTool` hard-block absolute paths / `cd ..` (breaks some workflows) or warn?

---

## 12. Explicit non-goals (this scope)

- Per-book model overrides.
- Multi-user/tenancy, RBAC, i18n.
- Mobile native app.
- Swapping the ASP.NET backend or Svelte frontend.
- Real-time collaborative editing.
