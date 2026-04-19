import { createServer, type IncomingMessage, type ServerResponse } from "http";
import { spawn, type ChildProcess } from "child_process";

const PORT = parseInt(process.env.PORT || "3001");
const PI_CWD = process.env.PI_CWD || "/library";
const PI_MODEL = process.env.PI_MODEL || "";
const MAX_RESTART_ATTEMPTS = 5;
const RESTART_DELAY_MS = 3000;

let agentProcess: ChildProcess | null = null;
let requestId = 0;
let restartAttempts = 0;
let buffer = "";

const pendingRequests = new Map<
  number,
  {
    resolve: (data: any) => void;
    reject: (err: Error) => void;
  }
>();

type EventCallback = (msg: any) => void;
const eventCallbacks = new Map<string, EventCallback>();

// --- Agent process lifecycle ---

function startAgent(): ChildProcess {
  if (restartAttempts >= MAX_RESTART_ATTEMPTS) {
    console.error(
      `[agent] max restart attempts (${MAX_RESTART_ATTEMPTS}) reached. Not restarting.`
    );
    return agentProcess!;
  }

  const args = ["--mode", "rpc", "--no-session", "--skill", "/skills/lore-extraction"];
  if (PI_MODEL) {
    args.push("--model", PI_MODEL);
  }
  const proc = spawn(
    "pi",
    args,
    {
      cwd: PI_CWD,
      stdio: ["pipe", "pipe", "pipe"],
      env: { ...process.env },
    }
  );

  proc.stderr?.on("data", (chunk: Buffer) => {
    console.error("[agent:stderr]", chunk.toString());
  });

  proc.stdout?.on("data", (chunk: Buffer) => {
    buffer += chunk.toString();
    processBuffer();
  });

  proc.on("exit", (code) => {
    console.error(
      `[agent] exited with code ${code}, restart attempt ${restartAttempts + 1}/${MAX_RESTART_ATTEMPTS} in ${RESTART_DELAY_MS}ms...`
    );
    restartAttempts++;
    setTimeout(() => {
      agentProcess = startAgent();
    }, RESTART_DELAY_MS);
  });

  console.log(
    `[agent] started (pid: ${proc.pid}, cwd: ${PI_CWD})`
  );
  restartAttempts = 0;
  return proc;
}

// --- JSONL framing ---

function processBuffer() {
  let newlineIndex: number;
  while ((newlineIndex = buffer.indexOf("\n")) !== -1) {
    let line = buffer.slice(0, newlineIndex);
    buffer = buffer.slice(newlineIndex + 1);
    if (line.endsWith("\r")) line = line.slice(0, -1);
    if (!line.trim()) continue;

    try {
      const msg = JSON.parse(line);
      handleAgentMessage(msg);
    } catch {
      console.error("[agent] failed to parse line:", line);
    }
  }
}

// --- Message routing ---

function handleAgentMessage(msg: any) {
  // Responses (have id + type "response") — resolve pending request
  if (msg.id && msg.type === "response") {
    const pending = pendingRequests.get(msg.id);
    if (pending) {
      pendingRequests.delete(msg.id);
      if (msg.success) {
        pending.resolve(msg.data);
      } else {
        pending.reject(
          new Error(msg.error || "Agent command failed")
        );
      }
    }
    return;
  }

  // All other messages are events — forward to SSE subscribers
  if (msg.type === "agent_start") {
    console.log(`[event] agent started`);
  } else if (msg.type === "message_update") {
    const delta = msg.assistantMessageEvent;
    if (delta?.type === "text_delta") {
      // Don't log every delta, too noisy
    } else if (delta?.type === "thinking_delta") {
      // Reasoning model — skip
    } else if (delta?.type === "thinking_start") {
      console.log(`[event] thinking started`);
    } else {
      console.log(`[event] message_update type=${delta?.type || "unknown"}`);
    }
  } else if (msg.type === "agent_end") {
    const assistantMsg = msg.messages?.find((m: any) => m.role === "assistant");
    const text = assistantMsg?.content?.find((c: any) => c.type === "text")?.text || "";
    const errMsg = assistantMsg?.errorMessage;
    console.log(`[event] agent_end: ${errMsg ? "ERROR: " + errMsg : `"${text.slice(0, 80)}${text.length > 80 ? "..." : ""}"`}`);
  } else {
    console.log(`[event] ${msg.type}`);
  }
  for (const callback of eventCallbacks.values()) {
    callback(msg);
  }
}

// --- Agent communication ---

function sendToAgent(command: any): Promise<any> {
  return new Promise((resolve, reject) => {
    if (!agentProcess || !agentProcess.stdin) {
      reject(new Error("Agent process not running"));
      return;
    }

    const id = ++requestId;
    const fullCommand = { ...command, id };
    pendingRequests.set(id, { resolve, reject });

    agentProcess.stdin.write(JSON.stringify(fullCommand) + "\n");

    setTimeout(() => {
      if (pendingRequests.has(id)) {
        pendingRequests.delete(id);
        reject(new Error("Agent request timed out (5 min)"));
      }
    }, 5 * 60 * 1000);
  });
}

/**
 * Send a prompt and collect the full assistant text response from events.
 * This is needed because the RPC `prompt` command only confirms the message
 * was queued — the actual response text arrives via `message_update` events.
 */
function sendPromptAndWaitForResponse(
  message: string
): Promise<string> {
  return new Promise((resolve, reject) => {
    if (!agentProcess || !agentProcess.stdin) {
      reject(new Error("Agent process not running"));
      return;
    }

    let fullText = "";
    const clientId = `collect-${++requestId}`;

    const cleanup = () => {
      eventCallbacks.delete(clientId);
    };

    const timeout = setTimeout(() => {
      cleanup();
      reject(new Error("Agent response timed out (5 min)"));
    }, 5 * 60 * 1000);

    eventCallbacks.set(clientId, (msg: any) => {
      if (msg.type === "message_update") {
        const delta = msg.assistantMessageEvent;
        if (delta?.type === "text_delta") {
          fullText += delta.delta;
        }
      } else if (msg.type === "agent_end") {
        clearTimeout(timeout);
        cleanup();
        resolve(fullText);
      } else if (msg.type === "extension_error") {
        clearTimeout(timeout);
        cleanup();
        reject(new Error(msg.error || "Extension error"));
      }
    });

    // Send the prompt command
    const cmdId = ++requestId;
    pendingRequests.set(cmdId, {
      resolve: () => {},
      reject: (err: Error) => {
        clearTimeout(timeout);
        cleanup();
        reject(err);
      },
    });

    agentProcess.stdin!.write(
      JSON.stringify({ type: "prompt", message, id: cmdId }) + "\n"
    );
  });
}

// --- HTTP server ---

async function handleRequest(req: IncomingMessage, res: ServerResponse) {
  const url = new URL(req.url || "/", `http://localhost:${PORT}`);

  if (req.method === "OPTIONS") {
    res.writeHead(200, corsHeaders());
    res.end();
    return;
  }

  if (url.pathname === "/health") {
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(
      JSON.stringify({
        status: "healthy",
        agentPid: agentProcess?.pid,
        agentAlive: agentProcess && !agentProcess.killed,
      })
    );
    return;
  }

  // Non-streaming prompt — collect full response from events
  if (url.pathname === "/api/prompt" && req.method === "POST") {
    console.log(`[http] POST /api/prompt`);
    try {
      const body = await readBody(req);
      const { message } = JSON.parse(body);
      console.log(`[http] prompt: "${message.slice(0, 80)}${message.length > 80 ? "..." : ""}" (${message.length} chars)`);
      const result = await sendPromptAndWaitForResponse(message);
      console.log(`[http] prompt response: ${result.length} chars`);
      res.writeHead(200, {
        "Content-Type": "application/json",
        ...corsHeaders(),
      });
      res.end(JSON.stringify({ success: true, data: result }));
    } catch (err: any) {
      sendError(res, 500, err.message);
    }
    return;
  }

  // SSE streaming prompt
  if (url.pathname === "/api/prompt/stream" && req.method === "POST") {
    console.log(`[http] POST /api/prompt/stream`);
    let body: string;
    try {
      body = await readBody(req);
    } catch (err: any) {
      sendError(res, 400, "Failed to read request body");
      return;
    }

    const { message } = JSON.parse(body);

    res.writeHead(200, {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache",
      Connection: "keep-alive",
      ...corsHeaders(),
    });

    const clientId = `sse-${Date.now()}`;

    // Register callback BEFORE sending prompt to avoid missing early events
    eventCallbacks.set(clientId, (msg: any) => {
      res.write(`data: ${JSON.stringify(msg)}\n\n`);

      if (msg.type === "agent_end") {
        setTimeout(() => {
          eventCallbacks.delete(clientId);
          res.end();
        }, 500);
      }
    });

    // Set a max lifetime for the SSE connection (5 min)
    const maxLifetime = setTimeout(() => {
      eventCallbacks.delete(clientId);
      res.write(
        `data: ${JSON.stringify({ type: "error", message: "Connection timed out" })}\n\n`
      );
      res.end();
    }, 5 * 60 * 1000);

    // Clear the max lifetime when connection closes normally
    res.on("close", () => {
      clearTimeout(maxLifetime);
      eventCallbacks.delete(clientId);
    });

    // Send prompt
    try {
      const cmdId = ++requestId;
      pendingRequests.set(cmdId, {
        resolve: () => {},
        reject: (err: Error) => {
          res.write(
            `data: ${JSON.stringify({ type: "error", message: err.message })}\n\n`
          );
          clearTimeout(maxLifetime);
          eventCallbacks.delete(clientId);
          res.end();
        },
      });
      agentProcess!.stdin!.write(
        JSON.stringify({ type: "prompt", message, id: cmdId }) + "\n"
      );
    } catch (err: any) {
      res.write(
        `data: ${JSON.stringify({ type: "error", message: err.message })}\n\n`
      );
      clearTimeout(maxLifetime);
      eventCallbacks.delete(clientId);
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
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
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

// --- Graceful shutdown ---

function shutdown() {
  console.log("[bridge] shutting down...");
  if (agentProcess && !agentProcess.killed) {
    agentProcess.kill("SIGTERM");
  }
  process.exit(0);
}

process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

// --- Start ---

agentProcess = startAgent();

const server = createServer(handleRequest);
server.listen(PORT, () => {
  console.log(`[bridge] HTTP server listening on port ${PORT}`);
});
