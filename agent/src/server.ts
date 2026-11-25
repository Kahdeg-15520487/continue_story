import { createServer, type IncomingMessage, type Server, type ServerResponse } from "http";
import { BACKGROUND_CONTEXT } from "@earendil-works/chord/context";
import type { AgentChange, ConversationId } from "@earendil-works/pi-durable";
import {
  currentModel,
  configuredProviders,
  findKeyForId,
  getOrCreateConversation,
  getSessionMap,
  modelCatalog,
  replaceConversation,
  usageFor,
  type ConversationMode,
  type DurableHarness,
} from "./harness.js";
import { LIBRARY_ROOT, PORT, SHARED_SLUG } from "./config.js";

function cors(): Record<string, string> {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, POST, PUT, DELETE, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type",
  };
}

function sendJson(res: ServerResponse, code: number, body: unknown): void {
  res.writeHead(code, { "Content-Type": "application/json", ...cors() });
  res.end(JSON.stringify(body));
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on("data", (c: Buffer) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

async function readJson(req: IncomingMessage): Promise<any> {
  const raw = await readBody(req);
  if (!raw.trim()) return {};
  return JSON.parse(raw);
}

function sse(body: unknown): string {
  return `data: ${JSON.stringify(body)}\n\n`;
}

function parseMode(value: unknown): ConversationMode {
  return value === "shared" ? "shared" : "story";
}

export function startServer(harness: DurableHarness): Server {
  const server = createServer(async (req, res) => {
    try {
      await handleRequest(harness, req, res);
    } catch (err: any) {
      console.error("[server] request failed:", err?.message ?? err);
      if (!res.headersSent) {
        sendJson(res, 500, { error: err?.message ?? "Internal error" });
      } else {
        try {
          res.end();
        } catch {}
      }
    }
  });
  server.listen(PORT, () => {
    const model = currentModel();
    console.log(`[server] durable agent listening on :${PORT} (model ${model.provider}/${model.modelId})`);
  });
  return server;
}

async function handleRequest(harness: DurableHarness, req: IncomingMessage, res: ServerResponse): Promise<void> {
  const url = new URL(req.url || "/", `http://localhost:${PORT}`);
  const path = url.pathname;
  const method = req.method || "GET";

  if (method === "OPTIONS") {
    res.writeHead(204, cors());
    res.end();
    return;
  }

  if (path === "/health" && method === "GET") {
    const books = await getSessionMap(harness);
    const model = currentModel();
    sendJson(res, 200, {
      status: "healthy",
      model: `${model.provider}/${model.modelId}`,
      conversations: Object.keys(books).length,
    });
    return;
  }

  if (path === "/api/models" && method === "GET") {
    const model = currentModel();
    sendJson(res, 200, { default: model.provider + "/" + model.modelId, providers: modelCatalog() });
    return;
  }

  // Phase 3 replaces this stub with the encrypted provider config.
  if (path === "/api/settings/llm" && method === "GET") {
    sendJson(res, 200, {
      providers: configuredProviders(),
      defaultProvider: currentModel().provider,
      defaultModelId: currentModel().modelId,
      thinkingLevel: "off",
    });
    return;
  }

  if (path === "/api/conversations" && method === "GET") {
    const books = await getSessionMap(harness);
    sendJson(res, 200, { conversations: Object.entries(books).map(([key, id]) => ({ key, id })) });
    return;
  }

  if (path === "/api/conversations" && method === "POST") {
    const body = await readJson(req);
    const slug = typeof body.slug === "string" ? body.slug : "";
    if (!slug || slug.includes("..") || slug.includes("/") || slug.includes("\\")) {
      sendJson(res, 400, { error: "Invalid slug" });
      return;
    }
    const mode = parseMode(body.mode);
    const { conversation, created, key } = await getOrCreateConversation(harness, slug, mode);
    sendJson(res, 200, { conversationId: conversation.id, key, mode, created });
    return;
  }

  const convMatch = path.match(/^\/api\/conversations\/(\d+)(?:\/([a-z]+))?$/);
  if (convMatch) {
    const id = Number(convMatch[1]) as ConversationId;
    const action = convMatch[2];
    const conversation = await harness.conversation(id, BACKGROUND_CONTEXT);
    if (!conversation) {
      sendJson(res, 404, { error: "Conversation not found" });
      return;
    }

    if (!action && method === "GET") {
      const agent = await conversation.agent(BACKGROUND_CONTEXT);
      const key = await findKeyForId(harness, id);
      sendJson(res, 200, {
        id,
        key,
        agent: { model: agent.model, thinkingLevel: agent.thinkingLevel, cwd: agent.cwd },
        usage: await usageFor(harness, id),
      });
      return;
    }

    if (!action && method === "DELETE") {
      const key = await findKeyForId(harness, id);
      if (!key) {
        sendJson(res, 404, { error: "Conversation is not mapped to a book" });
        return;
      }
      const mode: ConversationMode = key === SHARED_SLUG ? "shared" : "story";
      const slug = mode === "shared" ? SHARED_SLUG : key;
      const replacement = await replaceConversation(harness, slug, mode);
      sendJson(res, 200, { conversationId: replacement.id, replaced: id });
      return;
    }

    if (action === "input" && method === "POST") {
      const body = await readJson(req);
      if (typeof body.content !== "string" || !body.content.trim()) {
        sendJson(res, 400, { error: "content is required" });
        return;
      }
      const whenBusy = body.whenBusy === "steer" || body.whenBusy === "followUp" || body.whenBusy === "reject" ? body.whenBusy : undefined;
      const submission = await conversation.submit(
        {
          type: "input",
          content: body.content,
          whenBusy,
          whenIdle: body.whenIdle === "reject" ? "reject" : undefined,
          requestId: typeof body.requestId === "string" ? body.requestId : undefined,
        },
        BACKGROUND_CONTEXT,
      );
      sendJson(res, 200, { submissionId: submission.id });
      return;
    }

    if (action === "abort" && method === "POST") {
      await conversation.abort(BACKGROUND_CONTEXT);
      sendJson(res, 200, { aborted: true });
      return;
    }

    if (action === "configure" && method === "POST") {
      const body = await readJson(req);
      const change: Record<string, unknown> = {};
      if (body.model && typeof body.model.provider === "string" && typeof body.model.modelId === "string") {
        change.model = { provider: body.model.provider, modelId: body.model.modelId };
      }
      if (typeof body.thinkingLevel === "string") change.thinkingLevel = body.thinkingLevel;
      if (typeof body.instructions === "string") change.instructions = body.instructions;
      if (typeof body.maxTurns === "number") change.maxTurns = body.maxTurns;
      if (typeof body.cwd === "string") {
        if (!body.cwd.startsWith(LIBRARY_ROOT)) {
          sendJson(res, 400, { error: `cwd must be under ${LIBRARY_ROOT}` });
          return;
        }
        change.cwd = body.cwd;
      }
      await conversation.configure(change as AgentChange, BACKGROUND_CONTEXT);
      sendJson(res, 200, { configured: true });
      return;
    }

    if (action === "compact" && method === "POST") {
      const body = await readJson(req);
      const taskId = await conversation.compact(
        typeof body.instructions === "string" ? body.instructions : undefined,
        BACKGROUND_CONTEXT,
      );
      sendJson(res, 200, { taskId });
      return;
    }

    if (action === "usage" && method === "GET") {
      sendJson(res, 200, { usage: await usageFor(harness, id) });
      return;
    }

    if (action === "stream" && method === "GET") {
      await handleStream(req, res, conversation);
      return;
    }

    sendJson(res, 405, { error: `Unsupported ${method} ${path}` });
    return;
  }

  sendJson(res, 404, { error: "Not found" });
}

async function handleStream(req: IncomingMessage, res: ServerResponse, conversation: Awaited<ReturnType<DurableHarness["conversation"]>>): Promise<void> {
  if (!conversation) {
    sendJson(res, 404, { error: "Conversation not found" });
    return;
  }
  const watch = await conversation.watch(BACKGROUND_CONTEXT);
  res.writeHead(200, {
    "Content-Type": "text/event-stream",
    "Cache-Control": "no-cache, no-transform",
    Connection: "keep-alive",
    "X-Accel-Buffering": "no",
    ...cors(),
  });
  res.flushHeaders();
  res.write(sse({ v: 1, kind: "snapshot", value: watch.value }));

  let closed = false;
  const stop = () => {
    if (closed) return;
    closed = true;
    void watch.stop().catch(() => {});
    try {
      res.end();
    } catch {}
  };

  watch.start(async (call) => {
    if (closed || call.kind !== "value") return;
    try {
      res.write(sse({ v: 1, kind: "ops", ops: call.ops }));
    } catch {
      stop();
    }
  });

  req.on("close", stop);
  res.on("close", stop);
}
