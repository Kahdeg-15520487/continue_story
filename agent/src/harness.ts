import { mkdirSync } from "fs";
import { Harness, UsageDoc, createRegistry, defineDoc } from "@earendil-works/pi-durable";
import type { AgentChange, Conversation, ConversationId, EntryId } from "@earendil-works/pi-durable";
import { CodingTools } from "@earendil-works/pi-durable/tools";
import { NodeExecutionEnv } from "@earendil-works/pi-durable/env/node";
import { openNodeSqliteStorage } from "@earendil-works/pi-durable/storage/sqlite/node";
import { BACKGROUND_CONTEXT } from "@earendil-works/chord/context";
import { getDefaultModel, getModels, getProviderCatalog, getSettings } from "./llm.js";
import { WebTools } from "./tools/web.js";
import { KnowledgeExtension } from "./extensions/knowledge.js";
import { StoryExtension } from "./extensions/story.js";
import { InvariantsExtension } from "./extensions/invariants.js";
import { DURABLE_DB, LIBRARY_ROOT, SHARED_SLUG, durableDir } from "./config.js";

export type DurableHarness = Awaited<ReturnType<typeof Harness.open>>;
export type ConversationMode = "story" | "shared";

// ── Durable state ───────────────────────────────────────────────────

export const SessionsDoc = defineDoc({
  kind: "app.sessions",
  version: 1,
  scope: "session" as const,
  initial: () => ({ books: {} as Record<string, ConversationId> }),
  checkpointWhen: () => false,
});

const PromptVersionDoc = defineDoc({
  kind: "app.meta",
  version: 1,
  scope: "session" as const,
  initial: () => ({ promptVersion: 0 }),
  checkpointWhen: () => false,
});

const PROMPT_VERSION = 2;

// ── Providers (Phase 3 replaces env credentials with the encrypted config) ──

const models = getModels();

const registry = createRegistry();
registry.install(CodingTools);
registry.install(WebTools);
registry.install(StoryExtension);
registry.install(KnowledgeExtension);
registry.install(InvariantsExtension);

/** Settings are read at every use, so getters keep them live. */
function liveSettings() {
  return {
    get stream() {
      return { timeoutMs: 300_000 };
    },
    get retry() {
      return { maxRetries: 2 };
    },
    get compaction() {
      return { enabled: true, reserveTokens: 16_384 };
    },
  };
}

// ── Harness ─────────────────────────────────────────────────────────

export async function openHarness(): Promise<DurableHarness> {
  mkdirSync(durableDir(), { recursive: true });
  const storage = await openNodeSqliteStorage(DURABLE_DB);
  const harness = await Harness.open(
    storage,
    {
      models,
      registry,
      identity: {
        attribution: { name: "continue-story", url: "https://cs.minhnguyenle.work" },
      },
      env: ({ cwd }) => new NodeExecutionEnv({ cwd: cwd ?? LIBRARY_ROOT }),
      settings: liveSettings(),
    },
    BACKGROUND_CONTEXT,
  );
  harness.resume();
  await ensureConversationsConfigured(harness);
  return harness;
}

// ── Conversations ───────────────────────────────────────────────────

export function conversationKey(slug: string, mode: ConversationMode): string {
  return mode === "shared" ? SHARED_SLUG : slug;
}

export function conversationCwd(slug: string, mode: ConversationMode): string {
  return mode === "shared" ? LIBRARY_ROOT : `${LIBRARY_ROOT}/${slug}`;
}

export async function configureConversation(conv: Conversation, slug: string, mode: ConversationMode): Promise<void> {
  const cwd = conversationCwd(slug, mode);
  mkdirSync(cwd, { recursive: true });
  const llm = getDefaultModel();
  const change: AgentChange = {
    model: { provider: llm.provider, modelId: llm.modelId },
    thinkingLevel: llm.thinkingLevel as AgentChange["thinkingLevel"],
    // Craft rules come from registry sections; clear the Phase 1 stored prompt.
    instructions: null,
    cwd,
    maxTurns: 50,
    extensions: mode === "shared" ? { remove: [StoryExtension] } : { remove: [KnowledgeExtension] },
  };
  await conv.configure(change, BACKGROUND_CONTEXT);
}

/** One-time migration for conversations that stored the prompt as `instructions`. */
/** Re-apply the current model/sections to every mapped conversation. */
export async function reconfigureAll(harness: DurableHarness): Promise<void> {
  const books = await getSessionMap(harness);
  for (const [key, id] of Object.entries(books)) {
    const mode: ConversationMode = key === SHARED_SLUG ? "shared" : "story";
    const slug = mode === "shared" ? SHARED_SLUG : key;
    const conversation = await harness.conversation(id, BACKGROUND_CONTEXT);
    if (conversation) await configureConversation(conversation, slug, mode);
  }
}

export async function ensureConversationsConfigured(harness: DurableHarness): Promise<void> {
  const meta = await harness.snapshot(PromptVersionDoc, BACKGROUND_CONTEXT);
  if ((meta?.promptVersion ?? 0) >= PROMPT_VERSION) return;

  await reconfigureAll(harness);

  await harness.commit(async (tx) => {
    const doc = await tx.doc(PromptVersionDoc);
    doc.promptVersion = PROMPT_VERSION;
  }, BACKGROUND_CONTEXT);
}

export async function getOrCreateConversation(harness: DurableHarness, slug: string, mode: ConversationMode) {
  const key = conversationKey(slug, mode);
  const result = await harness.commit(async (tx) => {
    const doc = await tx.doc(SessionsDoc);
    const existing = doc.books[key];
    if (existing !== undefined) return { id: existing, created: false };
    const conv = await tx.createConversation({ ownership: { kind: "ownerless" } });
    doc.books[key] = conv.id;
    return { id: conv.id, created: true };
  }, BACKGROUND_CONTEXT);

  const conversation = await harness.conversation(result.id, BACKGROUND_CONTEXT);
  if (!conversation) throw new Error(`conversation ${result.id} for "${key}" vanished`);
  if (result.created) await configureConversation(conversation, slug, mode);
  return { conversation, created: result.created, key };
}

/** Clear the context, keeping the transcript (pi-durable `reset`). */
export async function resetConversation(harness: DurableHarness, id: ConversationId, handoff?: string): Promise<boolean> {
  const conversation = await harness.conversation(id, BACKGROUND_CONTEXT);
  if (!conversation) return false;
  await conversation.reset(handoff, BACKGROUND_CONTEXT);
  return true;
}

/** Branch the transcript (pi-durable `fork`). The fork is not book-mapped. */
export async function forkConversation(
  harness: DurableHarness,
  id: ConversationId,
  at?: EntryId,
): Promise<Conversation | undefined> {
  const conversation = await harness.conversation(id, BACKGROUND_CONTEXT);
  if (!conversation) return undefined;
  let entry = at;
  if (entry === undefined) {
    const view = await conversation.context(BACKGROUND_CONTEXT);
    entry = view.entries.at(-1)?.id;
  }
  if (entry === undefined) return undefined;
  return conversation.fork(entry, { ownership: { kind: "ownerless" } }, BACKGROUND_CONTEXT);
}

/** Remove the slug→conversation mapping; the transcript stays in durable storage. */
export async function unmapConversation(harness: DurableHarness, id: ConversationId): Promise<boolean> {
  const key = await findKeyForId(harness, id);
  if (!key) return false;
  await harness.commit(async (tx) => {
    const doc = await tx.doc(SessionsDoc);
    const books = doc.books as Record<string, ConversationId | undefined>;
    delete books[key];
  }, BACKGROUND_CONTEXT);
  return true;
}

export async function getSessionMap(harness: DurableHarness): Promise<Record<string, ConversationId>> {
  const snapshot = await harness.snapshot(SessionsDoc, BACKGROUND_CONTEXT);
  return (snapshot?.books ?? {}) as Record<string, ConversationId>;
}

export async function findKeyForId(harness: DurableHarness, id: ConversationId): Promise<string | undefined> {
  const books = await getSessionMap(harness);
  return Object.entries(books).find(([, value]) => value === id)?.[0];
}

/** Complete listing (built-in scan), annotated with the app's slug index. */
export async function listConversations(harness: DurableHarness) {
  const page = await harness.commit(
    (tx) => tx.scanConversations({ order: "descending" }, 200),
    BACKGROUND_CONTEXT,
  );
  const books = await getSessionMap(harness);
  const byId = new Map<number, string>();
  for (const [key, id] of Object.entries(books)) byId.set(Number(id), key);
  return page.items.map((record) => ({
    id: record.id,
    key: byId.get(Number(record.id)) ?? null,
  }));
}

export async function usageFor(harness: DurableHarness, id: ConversationId) {
  return harness.snapshot(UsageDoc, id, BACKGROUND_CONTEXT);
}

// ── Catalog ─────────────────────────────────────────────────────────

export function modelCatalog() {
  return getProviderCatalog();
}

export function configuredProviders() {
  return getSettings().providers.map((provider) => ({
    id: provider.id,
    configured: provider.configured,
  }));
}

export function currentModel() {
  const llm = getDefaultModel();
  return { provider: llm.provider, modelId: llm.modelId };
}
