import { createCipheriv, createDecipheriv, randomBytes } from "crypto";
import { existsSync, mkdirSync, readFileSync, renameSync, writeFileSync } from "fs";
import { dirname, join } from "path";
import { createModels, createProvider } from "@earendil-works/pi-ai/models";
import type { MutableModels } from "@earendil-works/pi-ai/models";
import { deepseekProvider } from "@earendil-works/pi-ai/providers/deepseek";
import { openaiProvider } from "@earendil-works/pi-ai/providers/openai";
import { anthropicProvider } from "@earendil-works/pi-ai/providers/anthropic";
import { googleProvider } from "@earendil-works/pi-ai/providers/google";
import { openrouterProvider } from "@earendil-works/pi-ai/providers/openrouter";
import { openAICompletionsApi } from "@earendil-works/pi-ai/api/openai-completions.lazy";
import { anthropicMessagesApi } from "@earendil-works/pi-ai/api/anthropic-messages.lazy";
import { googleGenerativeAIApi } from "@earendil-works/pi-ai/api/google-generative-ai.lazy";
import { openAIResponsesApi } from "@earendil-works/pi-ai/api/openai-responses.lazy";

// ── Types ───────────────────────────────────────────────────────────

export type EncryptedKey = { v: 1; iv: string; tag: string; ct: string };
export type ProviderKind = "deepseek" | "openai" | "anthropic" | "google" | "openrouter" | "custom";

export type ProviderEntry = {
  id: string;
  name?: string;
  kind: ProviderKind;
  baseUrl?: string;
  api?: string;
  keyEnc?: EncryptedKey;
  keyLast4?: string;
  models?: Array<{ id: string; name?: string }>;
  enabled?: boolean;
};

export type LlmConfig = {
  defaultProvider: string;
  defaultModel: string;
  thinkingLevel: string;
  providers: Record<string, ProviderEntry>;
};

export type ProviderInput = {
  id: string;
  name?: string;
  kind?: ProviderKind;
  baseUrl?: string;
  api?: string;
  apiKey?: string;
  models?: Array<{ id: string; name?: string }>;
};

// ── Constants ───────────────────────────────────────────────────────

const ENV_KEYS: Record<string, string> = {
  deepseek: "DEEPSEEK_API_KEY",
  openai: "OPENAI_API_KEY",
  anthropic: "ANTHROPIC_API_KEY",
  google: "GOOGLE_API_KEY",
  openrouter: "OPENROUTER_API_KEY",
};

const BUILTIN_FACTORIES: Record<string, () => ReturnType<typeof deepseekProvider>> = {
  deepseek: deepseekProvider,
  openai: openaiProvider as any,
  anthropic: anthropicProvider as any,
  google: googleProvider as any,
  openrouter: openrouterProvider as any,
};

const API_FACTORIES: Record<string, () => any> = {
  "openai-completions": openAICompletionsApi,
  "anthropic-messages": anthropicMessagesApi,
  "google-generative-ai": googleGenerativeAIApi,
  "openai-responses": openAIResponsesApi,
};

const DEFAULT_API: Record<ProviderKind, string> = {
  deepseek: "openai-completions",
  openai: "openai-responses",
  anthropic: "anthropic-messages",
  google: "google-generative-ai",
  openrouter: "openai-completions",
  custom: "openai-completions",
};

const CONFIG_PATH =
  process.env.LLM_CONFIG ||
  join(dirname(process.env.DURABLE_DB || "/data/durable.sqlite"), "llm-config.json");

const ID_PATTERN = /^[a-z0-9][a-z0-9_-]*$/;

// ── Encryption ──────────────────────────────────────────────────────

function encryptionKey(): Buffer | undefined {
  const raw = process.env.CONFIG_ENCRYPTION_KEY;
  if (!raw) return undefined;
  const key = Buffer.from(raw, "base64");
  if (key.length !== 32) throw new Error("CONFIG_ENCRYPTION_KEY must be 32 bytes, base64 encoded");
  return key;
}

function encrypt(plain: string): EncryptedKey {
  const key = encryptionKey();
  if (!key) throw new Error("CONFIG_ENCRYPTION_KEY is not configured; refusing to store an API key");
  const iv = randomBytes(12);
  const cipher = createCipheriv("aes-256-gcm", key, iv);
  const ct = Buffer.concat([cipher.update(plain, "utf8"), cipher.final()]);
  return {
    v: 1,
    iv: iv.toString("base64"),
    tag: cipher.getAuthTag().toString("base64"),
    ct: ct.toString("base64"),
  };
}

function decrypt(encrypted: EncryptedKey): string {
  const key = encryptionKey();
  if (!key) throw new Error("CONFIG_ENCRYPTION_KEY is not configured; cannot read the stored API key");
  const decipher = createDecipheriv("aes-256-gcm", key, Buffer.from(encrypted.iv, "base64"));
  decipher.setAuthTag(Buffer.from(encrypted.tag, "base64"));
  return Buffer.concat([
    decipher.update(Buffer.from(encrypted.ct, "base64")),
    decipher.final(),
  ]).toString("utf8");
}

// ── Config load / save ──────────────────────────────────────────────

let currentConfig: LlmConfig | undefined;
let modelsInstance: MutableModels | undefined;

function seedFromEnv(): LlmConfig {
  const [rawProvider, ...rest] = (process.env.PI_MODEL || "deepseek/deepseek-flash").split("/");
  const known = rawProvider in BUILTIN_FACTORIES;
  const provider = known ? rawProvider : "deepseek";
  const model = known ? rest.join("/") || "deepseek-flash" : "deepseek-flash";
  const providers: Record<string, ProviderEntry> = {};

  for (const [kind, envVar] of Object.entries(ENV_KEYS)) {
    if (process.env[envVar]) {
      providers[kind] = { id: kind, name: kind, kind: kind as ProviderKind, enabled: true };
    }
  }
  if (!providers[provider]) {
    providers[provider] = { id: provider, name: provider, kind: provider as ProviderKind, enabled: true };
  }

  return { defaultProvider: provider, defaultModel: model, thinkingLevel: "off", providers };
}

function normalize(raw: any): LlmConfig {
  const seed = seedFromEnv();
  const config: LlmConfig = {
    defaultProvider: typeof raw?.defaultProvider === "string" ? raw.defaultProvider : seed.defaultProvider,
    defaultModel: typeof raw?.defaultModel === "string" ? raw.defaultModel : seed.defaultModel,
    thinkingLevel: typeof raw?.thinkingLevel === "string" ? raw.thinkingLevel : "off",
    providers: {},
  };

  if (raw?.providers && typeof raw.providers === "object") {
    for (const [id, value] of Object.entries<any>(raw.providers)) {
      if (!value || typeof value !== "object") continue;
      const kind = (value.kind ?? id) as ProviderKind;
      config.providers[id] = {
        id,
        name: value.name,
        kind,
        baseUrl: value.baseUrl,
        api: value.api,
        keyEnc: value.keyEnc,
        keyLast4: value.keyLast4,
        models: Array.isArray(value.models) ? value.models : undefined,
        enabled: value.enabled !== false,
      };
    }
  }

  for (const [id, entry] of Object.entries(seed.providers)) {
    if (!config.providers[id]) config.providers[id] = entry;
  }
  return config;
}

export function loadConfig(): LlmConfig {
  if (currentConfig) return currentConfig;
  try {
    if (existsSync(CONFIG_PATH)) {
      currentConfig = normalize(JSON.parse(readFileSync(CONFIG_PATH, "utf8")));
      return currentConfig;
    }
  } catch (err: any) {
    console.error(`[llm] failed to read ${CONFIG_PATH}: ${err?.message ?? err}`);
  }
  currentConfig = normalize({});
  saveConfig(currentConfig);
  return currentConfig;
}

export function saveConfig(config: LlmConfig): void {
  currentConfig = config;
  try {
    mkdirSync(dirname(CONFIG_PATH), { recursive: true });
    const temp = `${CONFIG_PATH}.tmp`;
    writeFileSync(temp, JSON.stringify(config, null, 2), { mode: 0o600 });
    renameSync(temp, CONFIG_PATH);
  } catch (err: any) {
    console.error(`[llm] failed to write ${CONFIG_PATH}: ${err?.message ?? err}`);
  }
}

// ── Keys ────────────────────────────────────────────────────────────

function envKey(id: string): string | undefined {
  const envVar = ENV_KEYS[id];
  return envVar ? process.env[envVar] : undefined;
}

function resolveKey(entry: ProviderEntry | undefined, id?: string): string | undefined {
  if (entry?.keyEnc) {
    try {
      return decrypt(entry.keyEnc);
    } catch (err: any) {
      console.error(`[llm] cannot decrypt key for ${entry.id}: ${err?.message ?? err}`);
    }
  }
  return envKey(entry?.id ?? id ?? "");
}

function credentialStore() {
  return {
    async read(providerId: string) {
      const config = loadConfig();
      const entry = config.providers[providerId];
      const key = entry ? resolveKey(entry) : envKey(providerId);
      return key ? { type: "api_key" as const, key } : undefined;
    },
    async list() {
      const config = loadConfig();
      return Object.values(config.providers)
        .filter((entry) => Boolean(resolveKey(entry)))
        .map((entry) => ({ providerId: entry.id, type: "api_key" as const }));
    },
    async modify() {
      return undefined;
    },
    async delete() {},
  };
}

// ── Models registry ─────────────────────────────────────────────────

function apiFor(name: string | undefined, kind: ProviderKind) {
  const factory = API_FACTORIES[name ?? DEFAULT_API[kind]] ?? openAICompletionsApi;
  return factory();
}

function staticAuth(key: string | undefined) {
  return {
    apiKey: {
      name: "API key",
      resolve: async () => (key ? { auth: { apiKey: key } } : undefined),
    },
  };
}

function customModel(entry: ProviderEntry, model: { id: string; name?: string }, api: string) {
  return {
    id: model.id,
    name: model.name ?? model.id,
    api,
    provider: entry.id,
    baseUrl: entry.baseUrl ?? "",
    reasoning: false,
    input: ["text"],
    cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 },
    contextWindow: 128_000,
    maxTokens: 8_192,
  } as any;
}

function buildCustomProvider(entry: ProviderEntry) {
  const key = resolveKey(entry);
  const api = entry.api ?? DEFAULT_API[entry.kind] ?? "openai-completions";
  const factory = BUILTIN_FACTORIES[entry.kind];
  const base = factory ? factory() : undefined;
  const inherited = base && entry.kind !== "custom" ? base.getModels() : [];
  const list = inherited.length > 0
    ? inherited
    : (entry.models ?? []).map((model) => customModel(entry, model, api));

  return createProvider({
    id: entry.id,
    name: entry.name ?? entry.id,
    baseUrl: entry.baseUrl,
    auth: staticAuth(key) as any,
    models: list as any,
    api: apiFor(api, entry.kind),
  });
}

export function getModels(): MutableModels {
  if (modelsInstance) return modelsInstance;

  const config = loadConfig();
  const models = createModels({
    credentials: credentialStore() as any,
    identity: { attribution: { name: "continue-story", url: "https://cs.minhnguyenle.work" } },
  });

  for (const factory of Object.values(BUILTIN_FACTORIES)) {
    models.setProvider(factory() as any);
  }

  for (const entry of Object.values(config.providers)) {
    if (entry.enabled === false) continue;
    if (entry.kind === "custom" || entry.baseUrl) {
      models.setProvider(buildCustomProvider(entry));
    }
  }

  modelsInstance = models;
  return models;
}

function refreshProvider(entry: ProviderEntry): void {
  const models = getModels();
  if (entry.kind === "custom" || entry.baseUrl) {
    models.setProvider(buildCustomProvider(entry));
    return;
  }
  const factory = BUILTIN_FACTORIES[entry.kind];
  if (factory) models.setProvider(factory() as any);
}

// ── Public settings API ─────────────────────────────────────────────

function mask(entry: ProviderEntry) {
  const models = getModels();
  return {
    id: entry.id,
    name: entry.name ?? entry.id,
    kind: entry.kind,
    baseUrl: entry.baseUrl ?? null,
    configured: Boolean(resolveKey(entry)),
    keyLast4: entry.keyLast4 ?? null,
    enabled: entry.enabled !== false,
    models: models.getModels(entry.id).map((model) => ({
      id: model.id,
      name: model.name,
      contextWindow: model.contextWindow,
      maxTokens: model.maxTokens,
    })),
  };
}

export function getSettings() {
  const config = loadConfig();
  const models = getModels();
  return {
    default: {
      provider: config.defaultProvider,
      model: config.defaultModel,
      thinkingLevel: config.thinkingLevel,
    },
    providers: Object.values(config.providers).map(mask),
    catalog: models.getProviders().map((provider) => ({
      id: provider.id,
      name: provider.name,
      models: models.getModels(provider.id).map((model) => ({
        id: model.id,
        name: model.name,
        contextWindow: model.contextWindow,
        maxTokens: model.maxTokens,
      })),
    })),
    encryption: Boolean(encryptionKey()),
  };
}

export function getDefaultModel() {
  const config = loadConfig();
  return {
    provider: config.defaultProvider,
    modelId: config.defaultModel,
    thinkingLevel: config.thinkingLevel,
  };
}

export function saveProvider(input: ProviderInput) {
  if (!input.id || !ID_PATTERN.test(input.id)) {
    throw new Error("Provider id must match /^[a-z0-9][a-z0-9_-]*$/");
  }
  const kind = (input.kind ?? "custom") as ProviderKind;
  if (kind !== "custom" && !BUILTIN_FACTORIES[kind]) {
    throw new Error(`Unknown provider kind: ${kind}`);
  }

  const config = loadConfig();
  const existing = config.providers[input.id];

  let keyEnc = existing?.keyEnc;
  let keyLast4 = existing?.keyLast4;
  if (input.apiKey) {
    keyEnc = encrypt(input.apiKey);
    keyLast4 = input.apiKey.slice(-4);
  }

  const entry: ProviderEntry = {
    id: input.id,
    name: input.name ?? existing?.name ?? input.id,
    kind,
    baseUrl: input.baseUrl ?? existing?.baseUrl,
    api: input.api ?? existing?.api,
    models: input.models ?? existing?.models,
    keyEnc,
    keyLast4,
    enabled: true,
  };
  config.providers[entry.id] = entry;

  if (!config.defaultProvider || !config.providers[config.defaultProvider]) {
    config.defaultProvider = entry.id;
    const available = getModels().getModels(entry.id);
    config.defaultModel = available[0]?.id ?? config.defaultModel;
  }

  saveConfig(config);
  refreshProvider(entry);
  return mask(entry);
}

export function deleteProvider(id: string) {
  const config = loadConfig();
  if (!config.providers[id]) return false;
  delete config.providers[id];
  if (config.defaultProvider === id) {
    const next = Object.keys(config.providers)[0];
    config.defaultProvider = next ?? "";
    config.defaultModel = next ? (getModels().getModels(next)[0]?.id ?? "") : "";
  }
  saveConfig(config);
  return true;
}

export function setDefaultModel(input: { provider?: string; modelId?: string; thinkingLevel?: string }) {
  const config = loadConfig();
  if (input.provider) {
    if (!config.providers[input.provider]) throw new Error(`Unknown provider: ${input.provider}`);
    config.defaultProvider = input.provider;
  }
  if (input.modelId) config.defaultModel = input.modelId;
  if (input.thinkingLevel) config.thinkingLevel = input.thinkingLevel;

  const models = getModels();
  if (config.defaultProvider && config.defaultModel) {
    const known = models.getModels(config.defaultProvider).some((model) => model.id === config.defaultModel);
    const entry = config.providers[config.defaultProvider];
    const custom = entry?.models?.some((model) => model.id === config.defaultModel);
    if (!known && !custom) {
      throw new Error(`Unknown model ${config.defaultProvider}/${config.defaultModel}`);
    }
  }

  saveConfig(config);
  return getDefaultModel();
}

export async function testProvider(id: string) {
  const config = loadConfig();
  const entry = config.providers[id];
  if (!entry) return { ok: false, error: `Unknown provider: ${id}` };
  if (!resolveKey(entry)) return { ok: false, error: "No API key configured for this provider" };

  const models = getModels();
  let available;
  try {
    available = await models.getAvailable(id);
  } catch (err: any) {
    return { ok: false, error: err?.message ?? String(err) };
  }
  if (available.length === 0) return { ok: false, error: "No credential resolved for this provider" };

  const preferred = config.defaultProvider === id ? config.defaultModel : undefined;
  const found = preferred ? available.find((candidate) => candidate.id === preferred) : undefined;
  const model = found ?? available[0];
  if (!model) return { ok: false, error: "No model available for this provider" };

  try {
    const message = await Promise.race([
      models.completeSimple(
        model,
        { messages: [{ role: "user", content: "Reply with the single word OK.", timestamp: Date.now() }] },
        { maxTokens: 8 },
      ),
      new Promise<never>((_, reject) => setTimeout(() => reject(new Error("Timed out after 30s")), 30_000)),
    ]);
    const content = (message as any)?.content;
    const text = Array.isArray(content)
      ? content.filter((block: any) => block?.type === "text").map((block: any) => block.text).join("")
      : typeof content === "string"
        ? content
        : "";
    return { ok: true, provider: id, model: model.id, sample: text.slice(0, 80) };
  } catch (err: any) {
    return { ok: false, error: err?.message ?? String(err) };
  }
}

export function getProviderCatalog() {
  const models = getModels();
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

export function configPath(): string {
  return CONFIG_PATH;
}
