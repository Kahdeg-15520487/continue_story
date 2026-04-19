# Agent Isolation via Pi SDK — Ephemeral Per-Session Instances

> **Worker note:** Execute this plan task-by-task. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Replace the single shared `pi --mode rpc` child process with in-process Pi SDK sessions. Each chat/lore request creates an ephemeral `AgentSession` scoped to one book's directory with restricted tools. Multiple concurrent sessions are fully isolated — different CWD, different tools, different message history, no shared state. No child process management, no JSONL parsing.

**Architecture:** The agent bridge becomes a **thin session manager** using `createAgentSession()` from the Pi SDK. Each session:
- Has `cwd: /library/{slug}` — the agent can only see that book
- Has restricted tools: `createReadOnlyTools(cwd)` for chat, `createCodingTools(cwd)` for lore
- Uses `SessionManager.inMemory()` — no disk persistence
- Auto-disposes after idle timeout
- Lives as an in-process object — no OS process spawning

The backend no longer talks to an HTTP bridge at all. Instead, **the bridge IS the backend** — the agent bridge exposes the same HTTP API, but internally uses SDK sessions instead of child processes.

**Tech Stack:**
- **Agent bridge (Node.js):** `createAgentSession()`, `session.subscribe()`, `session.prompt()` from `@mariozechner/pi-coding-agent`
- **Backend (.NET):** Unchanged — still calls `agent:3001` via HTTP
- **No new containers or dependencies** — `@mariozechner/pi-coding-agent` already installed

**Work Scope:**
- **In scope:** SDK-based session management, per-book CWD, tool restriction, idle disposal
- **Out of scope:** User authentication, resource limits (CPU/memory), container-level isolation

---

**Verification Strategy:**
- **Level:** build + manual curl test
- **Command:** `docker compose build && docker compose up -d` → create two books, chat with both simultaneously, verify separate sessions

---

## File Structure Mapping

```
agent/src/index.ts              # REWRITE — SDK session manager replaces child process bridge
agent/models.json               # NO CHANGE — custom provider config still needed
agent/package.json              # NO CHANGE — @mariozechner/pi-coding-agent already a dependency

backend/src/KnowledgeEngine.Api/
├── Services/IAgentService.cs   # NO CHANGE — same interface, backend doesn't know about sessions
├── Services/AgentService.cs    # NO CHANGE — still POSTs to agent:3001
├── Endpoints/ChatEndpoints.cs  # NO CHANGE — already calls AgentService
└── Endpoints/LoreEndpoints.cs  # NO CHANGE — already calls AgentService via LoreJobService

docker-compose.yml              # MODIFY — agent gets read-write library for lore writing
```

---

## Task 1: Rewrite Agent Bridge with Pi SDK

**Dependencies:** None
**Files:**
- Rewrite: `agent/src/index.ts`

### Step 1: Rewrite `agent/src/index.ts`

Replace the entire file:

```typescript
import { createServer, type IncomingMessage, type ServerResponse } from "http";
import {
  createAgentSession,
  createCodingTools,
  createReadOnlyTools,
  DefaultResourceLoader,
  getAgentDir,
  SessionManager,
  type AgentSession,
  type AgentSessionEvent,
} from "@mariozechner/pi-coding-agent";

const PORT = parseInt(process.env.PORT || "3001");
const MAX_SESSIONS = parseInt(process.env.MAX_SESSIONS || "10");
const SESSION_IDLE_TIMEOUT_MS = 5 * 60 * 1000; // 5 min idle → dispose
const SESSION_MAX_LIFETIME_MS = 30 * 60 * 1000; // 30 min hard limit

interface ManagedSession {
  id: string;
  bookSlug: string;
  session: AgentSession;
  unsubscribe: () => void;
  createdAt: number;
  lastActivity: number;
  idleTimer: ReturnType<typeof setTimeout>;
  maxLifetimeTimer: ReturnType<typeof setTimeout>;
  // For collecting non-streaming responses
  responseResolve: ((text: string) => void) | null;
  responseReject: ((err: Error) => void) | null;
  responseText: string;
}

const sessions = new Map<string, ManagedSession>();

// --- Session lifecycle ---

async function createSession(bookSlug: string, mode: "read" | "write"): Promise<ManagedSession> {
  const id = `${bookSlug}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
  const cwd = `/library/${bookSlug}`;
  const agentDir = getAgentDir();

  const tools = mode === "write" ? createCodingTools(cwd) : createReadOnlyTools(cwd);

  const loader = new DefaultResourceLoader({
    cwd,
    agentDir,
    skillsOverride: (current) => ({
      skills: current.skills.filter(s =>
        s.name === "lore-extraction"
      ),
      diagnostics: current.diagnostics,
    }),
  });
  await loader.reload();

  const { session } = await createAgentSession({
    cwd,
    agentDir,
    tools,
    resourceLoader: loader,
    sessionManager: SessionManager.inMemory(),
  });

  const managed: ManagedSession = {
    id,
    bookSlug,
    session,
    unsubscribe: () => {},
    createdAt: Date.now(),
    lastActivity: Date.now(),
    idleTimer: setTimeout(() => disposeSession(id, "idle timeout"), SESSION_IDLE_TIMEOUT_MS),
    maxLifetimeTimer: setTimeout(() => disposeSession(id, "max lifetime"), SESSION_MAX_LIFETIME_MS),
    responseResolve: null,
    responseReject: null,
    responseText: "",
  };

  // Subscribe to events for logging and non-streaming response collection
  managed.unsubscribe = session.subscribe((event: AgentSessionEvent) => {
    handleSessionEvent(managed, event);
  });

  sessions.set(id, managed);
  console.log(`[session:${id}] created for "${bookSlug}" (mode: ${mode}, cwd: ${cwd}, active: ${sessions.size})`);
  return managed;
}

function handleSessionEvent(session: ManagedSession, event: AgentSessionEvent) {
  switch (event.type) {
    case "message_start":
      console.log(`[session:${session.id}] message_start: model=${event.message?.model || ""}`);
      break;
    case "message_end": {
      const usage = event.message?.usage;
      const tokens = usage ? `in=${usage.input || 0} out=${usage.output || 0}` : "";
      console.log(`[session:${session.id}] message_end: ${tokens}`);
      break;
    }
    case "agent_end": {
      const text = event.messages?.find((m: any) => m.role === "assistant")
        ?.content?.find((c: any) => c.type === "text")?.text || "";
      console.log(`[session:${session.id}] agent_end: "${text.slice(0, 100)}${text.length > 100 ? "..." : ""}"`);
      // Resolve non-streaming promise
      if (session.responseResolve) {
        session.responseResolve(session.responseText);
        session.responseResolve = null;
        session.responseReject = null;
        session.responseText = "";
      }
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
      console.error(`[session:${session.id}] extension_error: ${event.error || "unknown"}`);
      if (session.responseReject) {
        session.responseReject(new Error(event.error || "Extension error"));
        session.responseResolve = null;
        session.responseReject = null;
        session.responseText = "";
      }
      break;
    case "tool_execution_start":
      console.log(`[session:${session.id}] tool: ${event.toolName}`);
      break;
    case "tool_execution_end":
      console.log(`[session:${session.id}] tool end: ${event.toolName} (${event.isError ? "error" : "ok"})`);
      break;
  }

  resetIdleTimer(session);
}

function disposeSession(id: string, reason: string) {
  const managed = sessions.get(id);
  if (!managed) return;
  console.log(`[session:${id}] disposing: ${reason}`);
  managed.unsubscribe();
  managed.session.dispose();
  clearTimeout(managed.idleTimer);
  clearTimeout(managed.maxLifetimeTimer);
  // Reject any pending response
  if (managed.responseReject) {
    managed.responseReject(new Error(`Session disposed: ${reason}`));
    managed.responseResolve = null;
    managed.responseReject = null;
  }
  sessions.delete(id);
}

function resetIdleTimer(session: ManagedSession) {
  clearTimeout(session.idleTimer);
  session.idleTimer = setTimeout(() => disposeSession(session.id, "idle timeout"), SESSION_IDLE_TIMEOUT_MS);
  session.lastActivity = Date.now();
}

function findSessionByBook(bookSlug: string): ManagedSession | undefined {
  for (const [, s] of sessions) {
    if (s.bookSlug === bookSlug) return s;
  }
  return undefined;
}

// --- HTTP server ---

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
        age: Math.round((Date.now() - s.createdAt) / 1000) + "s",
        idle: Math.round((Date.now() - s.lastActivity) / 1000) + "s",
      })),
    }));
    return;
  }

  // List sessions
  if (url.pathname === "/api/sessions" && req.method === "GET") {
    res.writeHead(200, { "Content": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({
      sessions: Array.from(sessions.values()).map(s => ({
        id: s.id,
        bookSlug: s.bookSlug,
        age: Math.round((Date.now() - s.createdAt) / 1000) + "s",
        idle: Math.round((Date.now() - s.lastActivity) / 1000) + "s",
      })),
    }));
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
      const { bookSlug, mode } = JSON.parse(body);
      if (!bookSlug || bookSlug.includes("..") || bookSlug.includes("/") || bookSlug.includes("\\")) {
        sendError(res, 400, "Invalid book slug");
        return;
      }
      // Reuse existing session for this book
      let managed = findSessionByBook(bookSlug);
      if (!managed) {
        managed = await createSession(bookSlug, mode === "write" ? "write" : "read");
      } else {
        resetIdleTimer(managed);
      }
      res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
      res.end(JSON.stringify({ sessionId: managed.id, bookSlug: managed.bookSlug }));
    } catch (err: any) {
      console.error("[session] create failed:", err.message);
      sendError(res, 500, `Failed to create session: ${err.message}`);
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
      console.log(`[session:${managed.id}] prompt: "${message.slice(0, 80)}..." (${message.length} chars)`);

      // Set up promise for response collection
      const responsePromise = new Promise<string>((resolve, reject) => {
        managed.responseResolve = resolve;
        managed.responseReject = reject;
        managed.responseText = "";
      });

      // Send prompt — agent_end event will resolve the promise
      await managed.session.prompt(message);

      const result = await responsePromise;
      console.log(`[session:${managed.id}] response: ${result.length} chars`);

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
    console.log(`[session:${managed.id}] stream prompt: "${message.slice(0, 80)}..." (${message.length} chars)`);

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
        setTimeout(() => {
          sseUnsubscribe();
          res.end();
        }, 500);
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

// --- Graceful shutdown ---

function shutdown() {
  console.log(`[bridge] shutting down, disposing ${sessions.size} sessions...`);
  for (const [id] of sessions) {
    disposeSession(id, "bridge shutdown");
  }
  process.exit(0);
}

process.on("SIGTERM", shutdown);
process.on("SIGINT", shutdown);

// --- Start ---

const server = createServer(handleRequest);
server.listen(PORT, () => {
  console.log(`[bridge] SDK session manager listening on port ${PORT} (max sessions: ${MAX_SESSIONS})`);
});
```

Key design decisions:
- **`createAgentSession({ cwd })`** — scopes the agent to `/library/{slug}`, no access to sibling books
- **`createReadOnlyTools(cwd)`** for chat — agent can read files but not write
- **`createCodingTools(cwd)`** for lore — agent can read and write lore files
- **`skillsOverride`** — only loads `lore-extraction` skill, nothing else
- **`SessionManager.inMemory()`** — no disk persistence, pure in-memory
- **Session reuse by bookSlug** — second chat with same book reuses existing session
- **Idle timeout (5 min)** — `session.dispose()` frees resources
- **Max lifetime (30 min)** — hard dispose regardless of activity
- **Max sessions (10)** — prevents resource exhaustion
- **No child processes** — all sessions are in-process SDK objects
- **`session.dispose()`** — proper cleanup

### Step 2: Build agent container

```bash
docker compose build agent
```

Expected: Build succeeds.

### Step 3: Commit

```bash
git add agent/src/index.ts
git commit -m "refactor(agent): replace child process bridge with Pi SDK session manager, per-book isolation, tool restriction"
```

---

## Task 2: Update Backend to Use Session-Aware API

**Dependencies:** Task 1 (needs new agent API)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/IAgentService.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Services/AgentService.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs`

### Step 1: Update `IAgentService.cs`

Replace the entire file:

```csharp
namespace KnowledgeEngine.Api.Services;

public interface IAgentService
{
    /// <summary>
    /// Ensure an agent session exists for the given book, returning the session ID.
    /// </summary>
    Task<string> EnsureSessionAsync(string bookSlug, string mode = "read", CancellationToken ct = default);

    /// <summary>
    /// Send a non-streaming prompt to a session and return the full response.
    /// </summary>
    Task<string> SendPromptAsync(string sessionId, string message, CancellationToken ct = default);

    /// <summary>
    /// Stream a prompt response from a session.
    /// Yields JSON-RPC event strings as they arrive.
    /// </summary>
    IAsyncEnumerable<string> StreamPromptAsync(string sessionId, string message, CancellationToken ct = default);
}
```

### Step 2: Update `AgentService.cs`

Replace the entire file:

```csharp
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KnowledgeEngine.Api.Services;

public class AgentService : IAgentService
{
    private readonly HttpClient _http;
    private readonly string _agentBaseUrl;
    private readonly ILogger<AgentService> _logger;

    public AgentService(HttpClient http, IConfiguration config, ILogger<AgentService> logger)
    {
        _http = http;
        _logger = logger;
        var host = config.GetValue<string>("Agent:Host") ?? "agent";
        var port = config.GetValue<int>("Agent:Port");
        if (port == 0) port = 3001;
        _agentBaseUrl = $"http://{host}:{port}";
        _logger.LogInformation("AgentService configured: {Url}", _agentBaseUrl);
    }

    public async Task<string> EnsureSessionAsync(string bookSlug, string mode = "read", CancellationToken ct = default)
    {
        _logger.LogInformation("Ensuring session for book: {Slug} (mode: {Mode})", bookSlug, mode);

        var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions",
            new StringContent(JsonSerializer.Serialize(new { bookSlug, mode }), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        var sessionId = result.GetProperty("sessionId").GetString()
            ?? throw new InvalidOperationException("Agent returned no sessionId");

        _logger.LogInformation("Session ready: {SessionId} for {Slug}", sessionId, bookSlug);
        return sessionId;
    }

    public async Task<string> SendPromptAsync(string sessionId, string message, CancellationToken ct = default)
    {
        _logger.LogInformation("Sending prompt to session {Session} ({Length} chars)", sessionId, message.Length);

        try
        {
            var response = await _http.PostAsync($"{_agentBaseUrl}/api/sessions/{sessionId}/prompt",
                new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (result.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
                return data.ToString() ?? "";

            _logger.LogWarning("Agent returned no data for session {Session}", sessionId);
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send prompt to session {Session}", sessionId);
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(string sessionId, string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Streaming to session {Session} ({Length} chars)", sessionId, message.Length);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_agentBaseUrl}/api/sessions/{sessionId}/prompt/stream");
        request.Content = new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith("data: "))
                yield return line["data: ".Length..];
        }
    }
}
```

### Step 3: Update `ChatEndpoints.cs`

Add session creation before streaming. Find:

```csharp
            var sessionId = await agentService.EnsureSessionAsync(req.BookSlug, ct);
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, fullPrompt, ct))
```

Replace with:

```csharp
            var sessionId = await agentService.EnsureSessionAsync(req.BookSlug, "read", ct);
            await foreach (var evt in agentService.StreamPromptAsync(sessionId, fullPrompt, ct))
```

### Step 4: Update `LoreJobService.cs`

The lore service uses `SendPromptAsync`. It needs to create a session with "write" mode. Find:

```csharp
        var sessionId = await agentService.EnsureSessionAsync(slug);
        var result = await agentService.SendPromptAsync(sessionId, fullPrompt);
```

Replace with:

```csharp
        var sessionId = await agentService.EnsureSessionAsync(slug, "write");
        var result = await agentService.SendPromptAsync(sessionId, fullPrompt);
```

### Step 5: Build

```bash
cd backend && dotnet build KnowledgeEngine.sln
```

Expected: Build succeeds with 0 errors.

### Step 6: Commit

```bash
git add backend/src/KnowledgeEngine.Api/Services/ backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs backend/src/KnowledgeEngine.Api/Services/LoreJobService.cs
git commit -m "refactor(backend): session-aware AgentService — ensureSession before prompt, pass mode (read/write)"
```

---

## Task 3: Update Docker Compose — Agent Needs Read-Write Library

**Dependencies:** None (can run in parallel with Tasks 1–2)
**Files:**
- Modify: `docker-compose.yml`

### Step 1: Change agent library mount from read-only to read-write

Find:

```yaml
      - library-data:/library:ro
```

Replace with:

```yaml
      - library-data:/library
```

### Step 2: Add MAX_SESSIONS env var

Find:

```yaml
      - PI_MODEL=${PI_MODEL:-}
```

Insert after:

```yaml
      - MAX_SESSIONS=${MAX_SESSIONS:-10}
```

### Step 3: Commit

```bash
git add docker-compose.yml
git commit -m "fix: agent read-write library for lore writing, add MAX_SESSIONS env"
```

---

## Task 4: End-to-End Verification

**Dependencies:** Tasks 1–3
**Files:** None (read-only verification)

### Step 1: Build all containers

```bash
cd J:/workspace2/llm/continue_story_4
docker compose down -v 2>/dev/null || true
docker compose build 2>&1
```

Expected: All 3 services build.

### Step 2: Start

```bash
docker compose up -d 2>&1
```

### Step 3: Wait for health

```bash
sleep 15
curl -f http://localhost:5000/api/health
```

Expected: Healthy.

### Step 4: Test session isolation

```bash
# Create two books
curl -s -X POST http://localhost:5000/api/books -H "Content-Type: application/json" -d '{"title":"Book A","author":"A"}'
curl -s -X POST http://localhost:5000/api/books -H "Content-Type: application/json" -d '{"title":"Book B","author":"B"}'

# Check agent health (0 sessions)
echo "=== Initial ==="
curl -s http://localhost:3001/health

# Upload content to both
echo "Content A" > /tmp/a.txt
echo "Content B" > /tmp/b.txt
curl -s -X POST http://localhost:5000/api/books/book-a/upload -F "file=@/tmp/a.txt"
curl -s -X POST http://localhost:5000/api/books/book-b/upload -F "file=@/tmp/b.txt"
sleep 8

# Create sessions for both books via agent API
echo "=== Create session for Book A ==="
curl -s -X POST http://localhost:3001/api/sessions -H "Content-Type: application/json" -d '{"bookSlug":"book-a","mode":"read"}'

echo ""
echo "=== Create session for Book B ==="
curl -s -X POST http://localhost:3001/api/sessions -H "Content-Type: application/json" -d '{"bookSlug":"book-b","mode":"read"}'

# Check sessions (should be 2)
echo ""
echo "=== Sessions ==="
curl -s http://localhost:3001/health

# Test full chat flow through backend
echo ""
echo "=== Chat with Book A ==="
timeout 30 curl -s -N http://localhost:5000/api/chat \
  -H "Content-Type: application/json" \
  -d '{"bookSlug":"book-a","message":"What is this book about?"}' 2>&1 | head -5

echo ""
echo "=== Sessions after chat ==="
curl -s http://localhost:3001/api/sessions
```

Expected:
- Sessions created for each book with different CWDs
- Chat streams a response
- Agent health shows session details

### Step 5: Clean up

```bash
docker compose down -v
```
