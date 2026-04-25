import { createServer, type IncomingMessage, type ServerResponse } from "http";
import { mkdirSync, readdirSync, statSync, unlinkSync, rmSync } from "fs";
import { join } from "path";
import {
  createAgentSession,
  createCodingTools,
  DefaultResourceLoader,
  getAgentDir,
  SessionManager,
  type AgentSession,
  type AgentSessionEvent,
} from "@mariozechner/pi-coding-agent";

const PORT = parseInt(process.env.PORT || "3001");
const MAX_SESSIONS = parseInt(process.env.MAX_SESSIONS || "10");
const SESSION_IDLE_TIMEOUT_MS = 5 * 60 * 1000; // 5 min idle → dispose
const SESSION_MAX_LIFETIME_MS = 60 * 60 * 1000; // 60 min hard limit
const COMPACT_THRESHOLD_TOKENS = 100_000; // auto-compact at ~100k tokens

interface ManagedSession {
  id: string;
  bookSlug: string;
  mode: string;
  session: AgentSession;
  unsubscribe: () => void;
  createdAt: number;
  lastActivity: number;
  idleTimer: ReturnType<typeof setTimeout>;
  maxLifetimeTimer: ReturnType<typeof setTimeout>;
  responseReject: ((err: Error) => void) | null;
  responseText: string;
  tokenCount: number;
}

const sessions = new Map<string, ManagedSession>();

function shortId(id: string): string {
  return id.split("-").slice(-2).join("-"); // last 2 segments e.g. "0400912-lffypm"
}

function getSessionDir(bookSlug: string): string {
  return `/home/piagent/.pi-sessions/${bookSlug}`;
}

async function createSession(bookSlug: string): Promise<ManagedSession> {
  const id = `${bookSlug}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
  const cwd = `/library/${bookSlug}`;
  const agentDir = getAgentDir();
  const sessionDir = getSessionDir(bookSlug);

  mkdirSync(sessionDir, { recursive: true });

  const tools = createCodingTools(cwd);

  const loader = new DefaultResourceLoader({
    cwd,
    agentDir,
  });
  await loader.reload();

  const { session } = await createAgentSession({
    cwd,
    agentDir,
    tools,
    resourceLoader: loader,
    sessionManager: SessionManager.create(sessionDir),
  });

  const managed: ManagedSession = {
    id,
    bookSlug,
    mode: "read-write",
    session,
    unsubscribe: () => {},
    createdAt: Date.now(),
    lastActivity: Date.now(),
    idleTimer: setTimeout(() => disposeSession(id, "idle timeout"), SESSION_IDLE_TIMEOUT_MS),
    maxLifetimeTimer: setTimeout(() => disposeSession(id, "max lifetime"), SESSION_MAX_LIFETIME_MS),
    responseReject: null,
    responseText: "",
    tokenCount: 0,
  };

  managed.unsubscribe = session.subscribe((event: AgentSessionEvent) => {
    handleSessionEvent(managed, event);
  });

  sessions.set(id, managed);
  console.log(`[session:${shortId(id)}] created for "${bookSlug}" (active: ${sessions.size})`);
  return managed;
}

async function restoreSession(bookSlug: string): Promise<ManagedSession | null> {
  const sessionDir = getSessionDir(bookSlug);
  const cwd = `/library/${bookSlug}`;
  const agentDir = getAgentDir();

  try {
    const tools = createCodingTools(cwd);
    const loader = new DefaultResourceLoader({
      cwd,
      agentDir,
    });
    await loader.reload();

    const { session, modelFallbackMessage } = await createAgentSession({
      cwd,
      agentDir,
      tools,
      resourceLoader: loader,
      sessionManager: SessionManager.continueRecent(sessionDir),
    });

    if (modelFallbackMessage) {
      console.log(`[session:restore] model fallback: ${modelFallbackMessage}`);
    }

    const id = `${bookSlug}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
    const managed: ManagedSession = {
      id,
      bookSlug,
      mode: "read-write",
      session,
      unsubscribe: () => {},
      createdAt: Date.now(),
      lastActivity: Date.now(),
      idleTimer: setTimeout(() => disposeSession(id, "idle timeout"), SESSION_IDLE_TIMEOUT_MS),
      maxLifetimeTimer: setTimeout(() => disposeSession(id, "max lifetime"), SESSION_MAX_LIFETIME_MS),
      responseReject: null,
      responseText: "",
      tokenCount: 0,
    };

    managed.unsubscribe = session.subscribe((event: AgentSessionEvent) => {
      handleSessionEvent(managed, event);
    });

    sessions.set(id, managed);
    const msgCount = session.agent?.state?.messages?.length ?? 0;
    console.log(`[session:${shortId(id)}] restored for "${bookSlug}" (${msgCount} msgs, active: ${sessions.size})`);
    return managed;
  } catch (err: any) {
    console.log(`[restore] no session for "${bookSlug}": ${err.message}`);
    return null;
  }
}

function handleSessionEvent(session: ManagedSession, event: AgentSessionEvent) {
  switch (event.type) {
    case "message_end": {
      const usage = event.message?.usage;
      if (usage) {
        const total = (usage.input || 0) + (usage.output || 0);
        session.tokenCount += total;
      }
      break;
    }
    case "agent_end": {
      const text = event.messages?.find((m: any) => m.role === "assistant")
        ?.content?.find((c: any) => c.type === "text")?.text || "";
      const tools = event.messages?.filter((m: any) => m.role === "tool").length ?? 0;
      console.log(`[session:${shortId(session.id)}] done: ${text.length} chars, ${tools} tool calls, ${session.tokenCount} tokens`);
      break;
    }
    case "message_update": {
      const delta = event.assistantMessageEvent;
      if (delta.type === "text_delta") {
        session.responseText += delta.delta;
      }
      break;
    }
    case "extension_error":
      console.error(`[session:${shortId(session.id)}] extension error: ${event.error || "unknown"}`);
      if (session.responseReject) {
        session.responseReject(new Error(event.error || "Extension error"));
        session.responseReject = null;
        session.responseText = "";
      }
      break;
    case "tool_execution_start": {
      const args = event.args;
      let argsStr: string;
      if (typeof args === "string") {
        argsStr = args.length > 200 ? args.slice(0, 200) + "..." : args;
      } else {
        argsStr = JSON.stringify(args);
        if (argsStr.length > 200) argsStr = argsStr.slice(0, 200) + "...";
      }
      console.log(`[session:${shortId(session.id)}] → ${event.toolName}(${argsStr})`);
      break;
    }
    case "tool_execution_end": {
      const result = event.result;
      let resultStr: string;
      if (typeof result === "string") {
        resultStr = result.length > 200 ? result.slice(0, 200) + "..." : result;
      } else if (result !== undefined && result !== null) {
        resultStr = JSON.stringify(result);
        if (resultStr.length > 200) resultStr = resultStr.slice(0, 200) + "...";
      } else {
        resultStr = "(no output)";
      }
      if (event.isError) {
        console.log(`[session:${shortId(session.id)}] ← ${event.toolName} ERROR: ${resultStr}`);
      } else {
        console.log(`[session:${shortId(session.id)}] ← ${event.toolName}: ${resultStr}`);
      }
      break;
    }
  }

  resetIdleTimer(session);
}

function resetLifetime(managed: ManagedSession) {
  clearTimeout(managed.maxLifetimeTimer);
  managed.maxLifetimeTimer = setTimeout(
    () => disposeSession(managed.id, "max lifetime"),
    SESSION_MAX_LIFETIME_MS,
  );
}

function disposeSession(id: string, reason: string) {
  const managed = sessions.get(id);
  if (!managed) return;
  console.log(`[session:${shortId(id)}] disposing: ${reason}`);
  managed.unsubscribe();
  managed.session.dispose();
  clearTimeout(managed.idleTimer);
  clearTimeout(managed.maxLifetimeTimer);
  if (managed.responseReject) {
    managed.responseReject(new Error(`Session disposed: ${reason}`));
    managed.responseReject = null;
  }

  // Delete session files on explicit client request
  if (reason === "client request") {
    try {
      const sessionDir = getSessionDir(managed.bookSlug);
      try {
        const files = readdirSync(sessionDir).filter(f => f.endsWith(".jsonl"));
        for (const f of files) {
          unlinkSync(join(sessionDir, f));
        }
        console.log(`[session:${shortId(id)}] cleaned ${files.length} files`);
      } catch {}
    } catch {}
  }

  sessions.delete(id);
}

function resetIdleTimer(session: ManagedSession) {
  clearTimeout(session.idleTimer);
  session.idleTimer = setTimeout(() => disposeSession(session.id, "idle timeout"), SESSION_IDLE_TIMEOUT_MS);
  session.lastActivity = Date.now();
}

async function handleRequest(req: IncomingMessage, res: ServerResponse) {
  const url = new URL(req.url || "/", `http://localhost:${PORT}`);

  if (req.method === "OPTIONS") {
    res.writeHead(200, corsHeaders());
    res.end();
    return;
  }

  // Health check
  if (url.pathname === "/health") {
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({
      status: "healthy",
      activeSessions: sessions.size,
      maxSessions: MAX_SESSIONS,
      sessions: Array.from(sessions.values()).map(s => ({
        id: s.id,
        bookSlug: s.bookSlug,
        mode: s.mode,
        age: Math.round((Date.now() - s.createdAt) / 1000) + "s",
        idle: Math.round((Date.now() - s.lastActivity) / 1000) + "s",
        tokenCount: s.tokenCount,
      })),
    }));
    return;
  }

  // List sessions
  if (url.pathname === "/api/sessions" && req.method === "GET") {
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({
      sessions: Array.from(sessions.values()).map(s => ({
        id: s.id,
        bookSlug: s.bookSlug,
        mode: s.mode,
        age: Math.round((Date.now() - s.createdAt) / 1000) + "s",
        idle: Math.round((Date.now() - s.lastActivity) / 1000) + "s",
        tokenCount: s.tokenCount,
      })),
    }));
    return;
  }

  // List sessions for a book
  const bookSessionsMatch = url.pathname.match(/^\/api\/books\/([^/]+)\/sessions$/);
  if (bookSessionsMatch && req.method === "GET") {
    const bookSlug = bookSessionsMatch[1];
    const bookSessions = Array.from(sessions.values())
      .filter(s => s.bookSlug === bookSlug)
      .map(s => ({
        id: s.id,
        bookSlug: s.bookSlug,
        age: Math.round((Date.now() - s.createdAt) / 1000) + "s",
        idle: Math.round((Date.now() - s.lastActivity) / 1000) + "s",
        tokenCount: s.tokenCount,
      }));
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({ sessions: bookSessions }));
    return;
  }

  // Create/get session for a book
  if (url.pathname === "/api/sessions" && req.method === "POST") {
    if (sessions.size >= MAX_SESSIONS) {
      sendError(res, 429, `Maximum ${MAX_SESSIONS} concurrent sessions`);
      return;
    }
    try {
      const body = await readBody(req);
      const { bookSlug, forceNew } = JSON.parse(body);
      if (!bookSlug || bookSlug.includes("..") || bookSlug.includes("/") || bookSlug.includes("\\")) {
        sendError(res, 400, "Invalid book slug");
        return;
      }

      let managed: ManagedSession | undefined;

      // If forceNew, skip reuse and create fresh
      if (!forceNew) {
        // Try to reuse existing in-memory session for this book
        managed = Array.from(sessions.values())
          .find(s => s.bookSlug === bookSlug);

        // No in-memory session — try to restore from persistent storage
        if (!managed) {
          managed = (await restoreSession(bookSlug)) ?? undefined;
        }
      }

      if (!managed) {
        managed = await createSession(bookSlug);
      } else {
        resetIdleTimer(managed);
      }

      res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
      res.end(JSON.stringify({
        sessionId: managed.id,
        bookSlug: managed.bookSlug,
        mode: managed.mode,
        messageCount: managed.session.agent?.state?.messages?.length ?? 0,
      }));
    } catch (err: any) {
      console.error("[session] create failed:", err.message);
      sendError(res, 500, `Failed to create session: ${err.message}`);
    }
    return;
  }

  // Session info
  const infoMatch = url.pathname.match(/^\/api\/sessions\/([^/]+)\/info$/);
  if (infoMatch && req.method === "GET") {
    const sessionId = infoMatch[1];
    const managed = sessions.get(sessionId);
    if (!managed) {
      sendError(res, 404, "Session not found");
      return;
    }
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({
      sessionId: managed.id,
      bookSlug: managed.bookSlug,
      mode: managed.mode,
      messageCount: managed.session.agent?.state?.messages?.length ?? 0,
      lastActivity: new Date(managed.lastActivity).toISOString(),
      tokenBudget: { used: managed.tokenCount, limit: COMPACT_THRESHOLD_TOKENS },
    }));
    return;
  }

  // Compact session
  const compactMatch = url.pathname.match(/^\/api\/sessions\/([^/]+)\/compact$/);
  if (compactMatch && req.method === "POST") {
    const sessionId = compactMatch[1];
    const managed = sessions.get(sessionId);
    if (!managed) {
      sendError(res, 404, "Session not found");
      return;
    }
    try {
      const body = await readBody(req);
      const { customInstructions } = JSON.parse(body || "{}");
      await managed.session.compact(customInstructions);
      managed.tokenCount = 0;
      res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
      res.end(JSON.stringify({ success: true, tokenCount: 0 }));
    } catch (err: any) {
      sendError(res, 500, `Compaction failed: ${err.message}`);
    }
    return;
  }

  // Delete a session
  const killMatch = url.pathname.match(/^\/api\/sessions\/([^/]+)$/);
  if (killMatch && req.method === "DELETE") {
    disposeSession(killMatch[1], "client request");
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({ disposed: true }));
    return;
  }

  // Non-streaming prompt
  const promptMatch = url.pathname.match(/^\/api\/sessions\/([^/]+)\/prompt$/);
  if (promptMatch && req.method === "POST") {
    const sessionId = promptMatch[1];
    const managed = sessions.get(sessionId);
    if (!managed) {
      sendError(res, 404, "Session not found");
      return;
    }
    try {
      const body = await readBody(req);
      const { message } = JSON.parse(body);
      console.log(`[session:${shortId(managed.id)}] prompt: "${message.slice(0, 80)}${message.length > 80 ? "..." : ""}" (${message.length} chars)`);

      managed.responseText = "";
      managed.responseReject = null;

      resetLifetime(managed);
      await managed.session.prompt(message);

      const result = managed.responseText;
      managed.responseReject = null;
      managed.responseText = "";
      console.log(`[session:${shortId(managed.id)}] response: ${result.length} chars`);

      res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
      res.end(JSON.stringify({ success: true, data: result }));
    } catch (err: any) {
      sendError(res, 500, err.message);
    }
    return;
  }

  // SSE streaming prompt
  const streamMatch = url.pathname.match(/^\/api\/sessions\/([^/]+)\/prompt\/stream$/);
  if (streamMatch && req.method === "POST") {
    const sessionId = streamMatch[1];
    const managed = sessions.get(sessionId);
    if (!managed) {
      sendError(res, 404, "Session not found");
      return;
    }
    let body: string;
    try {
      body = await readBody(req);
    } catch (err: any) {
      sendError(res, 400, "Failed to read body");
      return;
    }

    const { message } = JSON.parse(body);
    console.log(`[session:${shortId(managed.id)}] stream: "${message.slice(0, 80)}${message.length > 80 ? "..." : ""}" (${message.length} chars)`);

    res.writeHead(200, {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache",
      Connection: "keep-alive",
      ...corsHeaders(),
    });

    // Subscribe to session events and forward as SSE
    const sseUnsubscribe = managed.session.subscribe((event: AgentSessionEvent) => {
      res.write(`data: ${JSON.stringify(event)}\n\n`);

      if (event.type === "agent_end") {
        // Check if we need to compact + continue
        if (managed.tokenCount > COMPACT_THRESHOLD_TOKENS) {
          console.log(`[session:${shortId(managed.id)}] post-response auto-compacting (tokens: ${managed.tokenCount})`);
          managed.session.compact(
            "Summarize the conversation so far. Preserve: 1) What the user asked, 2) What changes were made and to which files, 3) What still needs to be done."
          ).then(async () => {
            managed.tokenCount = 0;
            console.log(`[session:${shortId(managed.id)}] compacted, continuing task`);
            // Send continuation prompt
            try {
              resetLifetime(managed);
              await managed.session.prompt("Review what you've done so far. If there are any remaining changes to complete the user's original request, continue. If everything is done, summarize what was changed.");
            } catch (err: any) {
              console.error(`[session:${shortId(managed.id)}] continuation failed: ${err.message}`);
              sseUnsubscribe();
              res.end();
            }
          }).catch((err: any) => {
            console.error(`[session:${shortId(managed.id)}] compact failed: ${err.message}`);
            sseUnsubscribe();
            res.end();
          });
        } else {
          setTimeout(() => {
            sseUnsubscribe();
            res.end();
          }, 500);
        }
      }
    });

    // Max lifetime for this SSE connection
    const maxLifetime = setTimeout(() => {
      sseUnsubscribe();
      res.write(`data: ${JSON.stringify({ type: "error", message: "Connection timed out" })}\n\n`);
      res.end();
    }, 5 * 60 * 1000);

    res.on("close", () => {
      clearTimeout(maxLifetime);
      sseUnsubscribe();
    });

    // Send the prompt
    try {
      resetLifetime(managed);
      await managed.session.prompt(message);
    } catch (err: any) {
      res.write(`data: ${JSON.stringify({ type: "error", message: err.message })}\n\n`);
      clearTimeout(maxLifetime);
      sseUnsubscribe();
      res.end();
    }
    return;
  }

  res.writeHead(404, { "Content-Type": "application/json", ...corsHeaders() });
  res.end(JSON.stringify({ error: "Not found" }));
}

function corsHeaders(): Record<string, string> {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, POST, DELETE, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type",
  };
}

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on("data", (c: Buffer) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks).toString()));
    req.on("error", reject);
  });
}

function sendError(res: ServerResponse, code: number, message: string) {
  res.writeHead(code, { "Content-Type": "application/json", ...corsHeaders() });
  res.end(JSON.stringify({ error: message }));
}

function shutdown() {
  console.log(`[server] shutting down, disposing ${sessions.size} sessions...`);
  for (const [id] of sessions) {
    disposeSession(id, "server shutdown");
  }
  process.exit(0);
}

process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

const server = createServer(handleRequest);
server.listen(PORT, () => {
  console.log(`[server] SDK session manager listening on port ${PORT} (max sessions: ${MAX_SESSIONS})`);
});
