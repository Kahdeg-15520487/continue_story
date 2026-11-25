# Phase 0 Spike — Durable Harness Findings

- **Date:** 2026-09-29
- **Status:** Complete — all Phase 0 goals validated
- **Spike location:** `agent/spike/` (gitignored; throwaway)
- **Plan:** `docs/engineering-discipline/plans/2026-09-29-durable-provider-ux-revamp.md`

## Verdict

pi-durable 1.6.0 is viable for this app. The plan's architecture holds:
durable conversations, SQLite storage, `watch()` snapshot+ops over SSE, built-in
coding tools, explicit-key provider auth, and crash resume all work. **Three
assumptions needed correction** (see "Corrections"), and all four resolvable open
questions are answered.

## Environment

| Item | Value |
|---|---|
| Node (spike host) | v25.6.1 |
| Node (pi-durable engine) | **>= 22.19.0** → pin `node:22.19-alpine` in the Dockerfile |
| `@earendil-works/pi-durable` | 1.6.0 (exact) |
| `@earendil-works/chord` | 1.6.0 (exact) |
| `@earendil-works/pi-ai` | 1.6.0 (exact) |
| `@earendil-works/pi-schema` | 1.6.0 (exact) |
| Storage | `openNodeSqliteStorage()` → `node:sqlite` (ExperimentalWarning; no native build) |
| Model | `deepseek/deepseek-flash` (live key) |

## Validated

| Script | What it proves | Result |
|---|---|---|
| `01-core.mjs` | `Harness.open` + SQLite + conversation + real DeepSeek call + reopen | `PONG`; 3 entries (`pi.user`, `pi.system`, `pi.assistant`); survives reopen |
| `02-auth.mjs` | Explicit API key via a `CredentialStore` with **`DEEPSEEK_API_KEY` unset** | `STOREKEY` returned; `getAvailable("deepseek")` = 2 models |
| `03-watch.mjs` | `watch()` + ops over an HTTP SSE endpoint + tools | 1 snapshot + 10 ops frames; `note.txt` written with `"hello durable"` |
| `04-docs.mjs` | `defineDoc` slug→conversation map committed atomically with the conversation | persists and resolves after reopen |
| `05-resume.mjs` | SIGKILL mid-run, reopen, `resume()` | `waitForIdle` → idle; final answer ~10k chars |
| `06-catalog.mjs` | Model catalog before a key exists | `getModels()` = 2 DeepSeek / 47 OpenAI models; `getAvailable()` = 0 without a key |

## Answers to the plan's open questions

1. **Conversation creation (resolved).**
   `tx.createConversation({ ownership: { kind: "ownerless" } })` inside
   `harness.commit(async (tx) => …)`, or `harness.createConversation(options, ctx)`.
   The map is stored via `defineDoc`:

   ```ts
   const Sessions = defineDoc({
     kind: "app.sessions", version: 1, scope: "session",
     initial: () => ({ books: {} }),
     checkpointWhen: () => false,
   });

   const convId = await harness.commit(async (tx) => {
     const conv = await tx.createConversation({ ownership: { kind: "ownerless" } });
     const doc = await tx.doc(Sessions);
     doc.books[slug] = conv.id;
     return conv.id;
   }, ctx);
   ```

2. **Provider auth (resolved).** Built-in factories stay arg-less
   (`deepseekProvider()`); they resolve keys through a `CredentialStore`.
   Inject explicit keys with:

   ```ts
   const models = createModels({ credentials: store }); // store.read(id) -> { type: "api_key", key }
   models.setProvider(deepseekProvider());
   ```

   No env var required. This is exactly the provider-config path.

3. **Catalog before a key (resolved).** `models.getModels(provider)` returns the
   static catalog with **no key**; `models.getAvailable(provider)` requires one
   (returns `[]` otherwise). Settings UI should use `getModels()` for the picker
   and `getAvailable()` to validate a key.

4. **`pi.live` shape (resolved).**

   ```ts
   type LiveState = {
     run?: LiveRun;                        // present while busy
     generation?: { attempt; message?; retry?; deferred? };  // streamed partial
     tools?: ToolSlot[];                   // current tool round (NOT "toolCalls")
     nestedTools?; compactions?; compactionFailure?; runFailure?;
   };
   type ToolSlot = {
     callId; name; status: "pending"|"running"|"sleeping"|"waiting"|"done";
     output?; droppedBytes?; droppedLines?; details?; diagnostics?;
     taskId?; entry?;
   };
   ```

   Other docs in the view: `pi.agent`, `pi.inbox`, `pi.provider`, `pi.usage`.

## Corrections to the plan

1. **Tool slots are `pi.live.tools`, not `toolCalls`.** Update the UI mapping.
2. **Resume leaves a partial `pi.assistant` entry** from the killed attempt; the
   retried run appends a second assistant entry. The frontend must render
   partial/aborted entries and avoid duplicating the answer.
3. **Two catalog APIs:** `getModels()` (static, pre-key) vs `getAvailable()`
   (credential-gated). The settings flow depends on the distinction.
4. **Node pin:** `node:22.19-alpine` (pi-durable engine floor).

## Operational notes

- `node:sqlite` emits `ExperimentalWarning` on every open; suppress in production
  logs or accept it.
- `harness.close(ctx)` followed by reopen works; SQLite WAL recovers cleanly after
  SIGKILL.
- `conversation.context(ctx)` returns raw entries; the assistant text is on
  `entry.model.content`.
- `watch.start(listener)` + `watch.value` + `watch.stop()`; ops are Chord `Op[]`.
- The spike directory is gitignored, so it never ships.

## Remaining owner decisions (not technical blockers)

- **Skills:** keep `/skills` lore-extraction as an extension section or a tool?
- **Auth boundary:** Settings writes provider keys; confirm Authelia/ingress is the
  only gate or add an admin-only check.
- **Backfill:** import existing `ChatMessages` into durable transcripts, or treat
  them as read-only legacy history?
