import { createServer, type IncomingMessage, type ServerResponse } from "http";
import { spawn, type ChildProcess } from "child_process";

const PORT = parseInt(process.env.PORT || "3001");
const PI_CWD = process.env.PI_CWD || "/library";

let agentProcess: ChildProcess | null = null;
let requestId = 0;
const pendingRequests = new Map<number, {
  resolve: (data: any) => void;
  reject: (err: Error) => void;
}>();
let buffer = "";

function startAgent(): ChildProcess {
  const proc = spawn("pi", ["--mode", "rpc", "--no-session", "--skill", "/skills/lore-extraction"], {
    cwd: PI_CWD,
    stdio: ["pipe", "pipe", "pipe"],
    env: { ...process.env },
  });

  proc.stderr?.on("data", (chunk: Buffer) => {
    console.error("[agent:stderr]", chunk.toString());
  });

  proc.stdout?.on("data", (chunk: Buffer) => {
    buffer += chunk.toString();
    processBuffer();
  });

  proc.on("exit", (code) => {
    console.error(`[agent] exited with code ${code}, restarting in 3s...`);
    setTimeout(startAgent, 3000);
  });

  console.log(`[agent] started (pid: ${proc.pid}, cwd: ${PI_CWD})`);
  return proc;
}

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

function handleAgentMessage(msg: any) {
  if (msg.id && msg.type === "response") {
    const pending = pendingRequests.get(msg.id);
    if (pending) {
      pendingRequests.delete(msg.id);
      if (msg.success) {
        pending.resolve(msg.data);
      } else {
        pending.reject(new Error(msg.error || "Agent command failed"));
      }
    }
    return;
  }

  for (const callback of eventCallbacks.values()) {
    callback(msg);
  }
}

type EventCallback = (msg: any) => void;
const eventCallbacks = new Map<string, EventCallback>();

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

    // Timeout after 5 minutes
    setTimeout(() => {
      if (pendingRequests.has(id)) {
        pendingRequests.delete(id);
        reject(new Error("Agent request timed out"));
      }
    }, 5 * 60 * 1000);
  });
}

async function handleRequest(req: IncomingMessage, res: ServerResponse) {
  const url = new URL(req.url || "/", `http://localhost:${PORT}`);

  if (req.method === "OPTIONS") {
    res.writeHead(200, corsHeaders());
    res.end();
    return;
  }

  if (url.pathname === "/health") {
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({ status: "healthy", agentPid: agentProcess?.pid }));
    return;
  }

  if (url.pathname === "/api/prompt" && req.method === "POST") {
    try {
      const body = await readBody(req);
      const { message } = JSON.parse(body);
      const result = await sendToAgent({ type: "prompt", message });
      res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
      res.end(JSON.stringify({ success: true, data: result }));
    } catch (err: any) {
      sendError(res, 500, err.message);
    }
    return;
  }

  if (url.pathname === "/api/prompt/stream" && req.method === "POST") {
    const body = await readBody(req);
    const { message } = JSON.parse(body);

    res.writeHead(200, {
      "Content-Type": "text/event-stream",
      "Cache-Control": "no-cache",
      "Connection": "keep-alive",
      ...corsHeaders(),
    });

    const clientId = `sse-${Date.now()}`;
    const callback: EventCallback = (msg) => {
      res.write(`data: ${JSON.stringify(msg)}\n\n`);
    };

    try {
      await sendToAgent({ type: "prompt", message });
    } catch (err: any) {
      res.write(`data: ${JSON.stringify({ type: "error", message: err.message })}\n\n`);
    }

    const cleanup = setTimeout(() => {
      eventCallbacks.delete(clientId);
      res.end();
    }, 10000);

    eventCallbacks.set(clientId, (msg: any) => {
      clearTimeout(cleanup);
      callback(msg);
      if (msg.type === "agent_end") {
        setTimeout(() => {
          eventCallbacks.delete(clientId);
          res.end();
        }, 500);
      }
    });

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

agentProcess = startAgent();

const server = createServer(handleRequest);
server.listen(PORT, () => {
  console.log(`[bridge] HTTP server listening on port ${PORT}`);
});
