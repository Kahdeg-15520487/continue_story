import { mkdirSync } from "fs";
import { Harness, UsageDoc, createRegistry, defineDoc } from "@earendil-works/pi-durable";
import type { AgentChange, Conversation, ConversationId } from "@earendil-works/pi-durable";
import { CodingTools } from "@earendil-works/pi-durable/tools";
import { NodeExecutionEnv } from "@earendil-works/pi-durable/env/node";
import { openNodeSqliteStorage } from "@earendil-works/pi-durable/storage/sqlite/node";
import { BACKGROUND_CONTEXT } from "@earendil-works/chord/context";
import { createModels } from "@earendil-works/pi-ai/models";
import { deepseekProvider } from "@earendil-works/pi-ai/providers/deepseek";
import { openaiProvider } from "@earendil-works/pi-ai/providers/openai";
import { anthropicProvider } from "@earendil-works/pi-ai/providers/anthropic";
import { googleProvider } from "@earendil-works/pi-ai/providers/google";
import { openrouterProvider } from "@earendil-works/pi-ai/providers/openrouter";
import { WebTools } from "./tools/web.js";
import { KB_INSTRUCTIONS, STORY_INSTRUCTIONS } from "./prompts.js";
import { DURABLE_DB, LIBRARY_ROOT, MODEL, SHARED_SLUG, durableDir } from "./config.js";

export type DurableHarness = Awaited<ReturnType<typeof Harness.open>>;
export type ConversationMode = "story" | "shared";

// ── Durable map: conversation key (book slug / "__shared__") → conversation id ──

export const SessionsDoc = defineDoc({
  kind: "app.sessions",
  version: 1,
  scope: "session" as const,
  initial: () => ({ books: {} as Record<string, ConversationId> }),
  checkpointWhen: () => false,
});

// ── Providers (Phase 3 replaces env credentials with the encrypted config) ──

const ENV_KEYS: Record<string, string> = {
  deepseek: "DEEPSEEK_API_KEY",
  openai: "OPENAI_API_KEY",
  anthropic: "ANTHROPIC_API_KEY",
  google: "GOOGLE_API_KEY",
  openrouter: "OPENROUTER_API_KEY",
};

const credentialStore = {
  async read(providerId: string) {
    const envVar = ENV_KEYS[providerId];
    const key = envVar ? process.env[envVar] : undefined;
    return key ? { type: "api_key" as const, key } : undefined;
  },
  async list() {
    return Object.entries(ENV_KEYS)
      .filter(([, envVar]) => Boolean(process.env[envVar]))
      .map(([providerId]) => ({ providerId, type: "api_key" as const }));
  },
  async modify() {
    return undefined;
  },
  async delete() {},
};

const models = createModels({ credentials: credentialStore });
for (const factory of [deepseekProvider, openaiProvider, anthropicProvider, googleProvider, openrouterProvider]) {
  models.setProvider(factory());
}

const registry = createRegistry();
registry.install(CodingTools);
registry.install(WebTools);

// ── Harness ─────────────────────────────────────────────────────────

export async function openHarness(): Promise<DurableHarness> {
  mkdirSync(durableDir(), { recursive: true });
  const storage = await openNodeSqliteStorage(DURABLE_DB);
  const harness = await Harness.open(
    storage,
    {
      models,
      registry,
      env: ({ cwd }) => new NodeExecutionEnv({ cwd: cwd ?? LIBRARY_ROOT }),
      settings: {
        stream: { timeoutMs: 300_000 },
        retry: { maxRetries: 2 },
        compaction: { enabled: true, reserveTokens: 16_384 },
      },
    },
    BACKGROUND_CONTEXT,
  );
  harness.resume();
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
  const change: AgentChange = {
    model: { provider: MODEL.provider, modelId: MODEL.modelId },
    thinkingLevel: "off",
    instructions: mode === "shared" ? KB_INSTRUCTIONS : STORY_INSTRUCTIONS,
    cwd,
    maxTurns: 50,
  };
  await conv.configure(change, BACKGROUND_CONTEXT);
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

/** Replace a conversation (pi-durable has no delete; the old transcript stays in storage, unreferenced). */
export async function replaceConversation(harness: DurableHarness, slug: string, mode: ConversationMode) {
  const key = conversationKey(slug, mode);
  const id = await harness.commit(async (tx) => {
    const doc = await tx.doc(SessionsDoc);
    const conv = await tx.createConversation({ ownership: { kind: "ownerless" } });
    doc.books[key] = conv.id;
    return conv.id;
  }, BACKGROUND_CONTEXT);
  const conversation = await harness.conversation(id, BACKGROUND_CONTEXT);
  if (!conversation) throw new Error(`replacement conversation ${id} vanished`);
  await configureConversation(conversation, slug, mode);
  return conversation;
}

export async function getSessionMap(harness: DurableHarness): Promise<Record<string, ConversationId>> {
  const snapshot = await harness.snapshot(SessionsDoc, BACKGROUND_CONTEXT);
  return (snapshot?.books ?? {}) as Record<string, ConversationId>;
}

export async function findKeyForId(harness: DurableHarness, id: ConversationId): Promise<string | undefined> {
  const books = await getSessionMap(harness);
  return Object.entries(books).find(([, value]) => value === id)?.[0];
}

export async function usageFor(harness: DurableHarness, id: ConversationId) {
  return harness.snapshot(UsageDoc, id, BACKGROUND_CONTEXT);
}

// ── Catalog ─────────────────────────────────────────────────────────

export function modelCatalog() {
  return models.getProviders().map((provider) => ({
    id: provider.id,
    name: provider.name,
    models: models.getModels(provider.id).map((model) => ({
      id: model.id,
      name: model.name,
      contextWindow: model.contextWindow,
      maxTokens: model.maxTokens,
      input: model.input,
    })),
  }));
}

export function configuredProviders() {
  return Object.entries(ENV_KEYS).map(([id, envVar]) => ({
    id,
    configured: Boolean(process.env[envVar]),
  }));
}

export function currentModel() {
  return MODEL;
}
