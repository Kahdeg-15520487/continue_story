# Missing Features & Bug Fixes Implementation Plan

> **Worker note:** Execute this plan task-by-task using the agentic-run-plan skill or subagents. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Fix 25 critical and moderate issues found in the Knowledge Engine codebase — agent container crashes on startup, Pi RPC non-streaming prompt path returns no content, missing input validation, frontend SSE parsing bugs, missing Milkdown theme CSS, panel UX issues (polling never times out, $effect leak), and dead service code.

**Architecture:** Fix existing code in-place across 3 containers (agent, api, frontend). No new services or dependencies. Each task is scoped to a self-contained set of files so tasks can run in parallel where dependencies allow.

**Tech Stack:**
- **Backend:** .NET 8 (keeping current version — plan originally specified .NET 9 but all packages are stable on 8)
- **Frontend:** SvelteKit 5, Milkdown v7
- **Agent:** Node.js 22, `@mariozechner/pi-coding-agent` RPC mode, tsx runtime

**Work Scope:**
- **In scope:** Agent Dockerfile/bridge fixes, backend validation + security, service cleanup, frontend editor/SSE/panel fixes, E2E verification
- **Out of scope:** .NET 8→9 upgrade (separate task, high risk), file upload UI, full-text search, authentication, test infrastructure

---

**Verification Strategy:**
- **Level:** build-only (no test infrastructure exists)
- **Command:** `docker compose build && docker compose up -d && sleep 15 && curl -f http://localhost:5000/api/health`
- **What it validates:** All containers build and start; backend health endpoint responds

---

## File Structure Mapping

```
agent/
├── Dockerfile                              # Task 1: fix CMD, install devDeps
├── package.json                            # Task 1: move tsx to dependencies
└── src/index.ts                            # Task 2: fix protocol, add shutdown handler

backend/
├── Dockerfile                              # No changes
└── src/KnowledgeEngine.Api/
    ├── Program.cs                          # Task 3: guard Hangfire dashboard
    ├── Endpoints/
    │   ├── LibraryEndpoints.cs             # Task 3: add validation, handle slug collision
    │   ├── EditorEndpoints.cs              # Task 3: add slug validation
    │   ├── ConversionEndpoints.cs          # Task 3: return 202
    │   ├── ChatEndpoints.cs                # Task 4: handle missing book
    │   └── LoreEndpoints.cs                # Task 3: add slug validation
    └── Services/
        ├── IConversionService.cs           # Task 4: add CancellationToken
        ├── ConversionService.cs            # Task 4: pass CancellationToken
        ├── IAgentService.cs                # No changes
        ├── AgentService.cs                 # Task 4: add logging
        ├── ILoreService.cs                 # Task 4: delete (dead code)
        └── LoreService.cs                  # Task 4: delete (dead code)

frontend/
├── src/lib/
│   ├── api.ts                              # Task 6: fix SSE parsing, add error handling
│   └── components/
│       ├── BookEditor.svelte               # Task 5: import theme CSS, add content reactivity
│       ├── ChatPanel.svelte                # Task 7: fix $effect leak
│       ├── LorePanel.svelte                # Task 7: add polling timeout
│       └── BookList.svelte                 # No changes
└── src/routes/
    ├── +page.svelte                        # Task 7: add error display, add delete button
    └── books/[slug]/+page.svelte           # Task 5: debounce save, Task 7: error feedback
```

---

## Task 1: Agent Container Startup Fix

**Dependencies:** None (can run in parallel)
**Files:**
- Modify: `agent/Dockerfile`
- Modify: `agent/package.json`

The agent container crashes immediately because: (a) `CMD ["node", "src/index.ts"]` — Node.js cannot execute TypeScript, and (b) `tsx` is in `devDependencies` which Docker skips by default.

- [ ] **Step 1: Move `tsx` and `@types/node` from devDependencies to dependencies in `agent/package.json`**

Replace the entire `package.json`:

```json
{
  "name": "knowledge-engine-agent",
  "version": "1.0.0",
  "private": true,
  "type": "module",
  "scripts": {
    "start": "npx tsx src/index.ts",
    "dev": "npx tsx watch src/index.ts"
  },
  "dependencies": {
    "@mariozechner/pi-coding-agent": "latest",
    "tsx": "^4.0.0",
    "@types/node": "^22.0.0"
  }
}
```

- [ ] **Step 2: Fix Dockerfile CMD and copy order in `agent/Dockerfile`**

Replace the entire `agent/Dockerfile`:

```dockerfile
FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY package.json ./
RUN npm install
COPY src/ ./src/
EXPOSE 3001
CMD ["npx", "tsx", "src/index.ts"]
```

Key changes: `package.json` copied before `src/` so `npm install` layer is cached; CMD uses `npx tsx`.

- [ ] **Step 3: Verify the fix locally**

```bash
cd J:/workspace2/llm/continue_story_4
docker compose build agent
```

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add agent/Dockerfile agent/package.json
git commit -m "fix(agent): use tsx runtime in Dockerfile, move tsx to dependencies"
```

---

## Task 2: Agent Bridge Protocol Fix

**Dependencies:** Task 1 (needs working container to test)
**Files:**
- Modify: `agent/src/index.ts`

The Pi RPC `prompt` command returns `{id, type: "response", command: "prompt", success: true}` with NO `data` field. The assistant's response text arrives via `message_update` events. The current non-streaming `/api/prompt` endpoint resolves with `undefined`, breaking `AgentService.SendPromptAsync()` and all lore generation.

The SSE `/api/prompt/stream` endpoint also has a race: the event callback is registered AFTER `sendToAgent()` resolves, so early events can be missed.

Additionally: no graceful shutdown handler (orphaned child process on Docker stop), no restart limit (infinite loop if Pi is misconfigured), and pending requests leak on agent crash.

- [ ] **Step 1: Rewrite `agent/src/index.ts`**

Replace the entire file:

```typescript
import { createServer, type IncomingMessage, type ServerResponse } from "http";
import { spawn, type ChildProcess } from "child_process";

const PORT = parseInt(process.env.PORT || "3001");
const PI_CWD = process.env.PI_CWD || "/library";
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

  const proc = spawn(
    "pi",
    ["--mode", "rpc", "--no-session", "--skill", "/skills/lore-extraction"],
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
    try {
      const body = await readBody(req);
      const { message } = JSON.parse(body);
      const result = await sendPromptAndWaitForResponse(message);
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
```

Key changes:
1. **`sendPromptAndWaitForResponse()`** — collects assistant text from `message_update` events until `agent_end`, returns the full text. Fixes the broken non-streaming path used by lore generation.
2. **SSE callback registered BEFORE sending prompt** — no more missed early events.
3. **`res.on("close")` cleanup** — handles client disconnect gracefully.
4. **Max SSE lifetime** (5 min) — prevents leaked connections.
5. **Restart limit** (5 attempts) — no infinite restart loop.
6. **SIGTERM/SIGINT handlers** — kills child process on Docker stop.
7. **Health check enhanced** — includes `agentAlive` boolean.

- [ ] **Step 2: Build and verify**

```bash
cd J:/workspace2/llm/continue_story_4
docker compose build agent
```

Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add agent/src/index.ts
git commit -m "fix(agent): collect response from events for non-streaming prompt, add graceful shutdown and restart limit"
```

---

## Task 3: Backend Validation + Security

**Dependencies:** None (can run in parallel)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/LibraryEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/EditorEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/LoreEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Endpoints/ConversionEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Program.cs`

- [ ] **Step 1: Add slug validation helper to `LibraryEndpoints.cs`**

Add this method at the bottom of the `LibraryEndpoints` class (before the closing `}`), after the existing `GenerateSlug` method:

```csharp
    private static string ValidateSlug(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            throw new ArgumentException("Slug cannot be empty");
        if (slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
            throw new ArgumentException("Slug contains invalid characters");
        return slug;
    }
```

- [ ] **Step 2: Add title validation and slug collision try-catch in `LibraryEndpoints.cs`**

In the `group.MapPost` handler, replace the existing slug check and book creation block:

Old:
```csharp
            var slug = GenerateSlug(req.Title);
            if (await db.Books.AnyAsync(b => b.Slug == slug))
                return Results.Conflict(new { error = "Book already exists" });
```

New:
```csharp
            if (string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Title is required" });

            var slug = GenerateSlug(req.Title);
            if (string.IsNullOrWhiteSpace(slug))
                return Results.BadRequest(new { error = "Title produces an invalid slug" });
```

Also wrap the `SaveChanges` call in a try-catch for the unique index constraint. Replace:

```csharp
            db.Books.Add(book);
            await db.SaveChangesAsync();

            return Results.Created($"/api/books/{slug}", new BookDetailDto(book));
```

With:

```csharp
            db.Books.Add(book);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "A book with this slug already exists" });
            }

            return Results.Created($"/api/books/{slug}", new BookDetailDto(book));
```

Add the missing `using` at the top of the file if not present:

```csharp
using Microsoft.EntityFrameworkCore;
```

(Already present — just verify.)

- [ ] **Step 3: Add slug validation to `EditorEndpoints.cs`**

At the start of each endpoint handler (`group.MapGet("/")` and `group.MapPut("/")`), add slug validation after the `slug` parameter. Replace the first line of each handler:

For GET `/`, replace:

```csharp
        group.MapGet("/", async (string slug, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

With:

```csharp
        group.MapGet("/", async (string slug, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

For PUT `/`, replace:

```csharp
        group.MapPut("/", async (string slug, UpdateContentRequest req, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

With:

```csharp
        group.MapPut("/", async (string slug, UpdateContentRequest req, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

For GET `/metadata`, replace:

```csharp
        group.MapGet("/metadata", async (string slug, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

With:

```csharp
        group.MapGet("/metadata", async (string slug, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

- [ ] **Step 4: Add slug validation to `ConversionEndpoints.cs`**

At the start of the POST handler, add validation. Replace:

```csharp
        group.MapPost("/", async (
            string slug,
            AppDbContext db,
            IConfiguration config,
            IBackgroundJobClient jobClient) =>
        {
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
```

With:

```csharp
        group.MapPost("/", async (
            string slug,
            AppDbContext db,
            IConfiguration config,
            IBackgroundJobClient jobClient) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
```

Also change the return from `Results.Ok` to `Results.Accepted` for proper HTTP semantics. Replace:

```csharp
            return Results.Ok(new { jobId, status = "queued" });
```

With:

```csharp
            return Results.Accepted(null, new { jobId, status = "queued" });
```

- [ ] **Step 5: Add slug validation to `LoreEndpoints.cs`**

In the POST handler, add validation. Replace:

```csharp
        group.MapPost("/", (string slug, IBackgroundJobClient jobClient) =>
        {
            var jobId = jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
```

With:

```csharp
        group.MapPost("/", (string slug, IBackgroundJobClient jobClient) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var jobId = jobClient.Enqueue<LoreJobService>(x => x.GenerateLoreAsync(slug));
```

In the GET `/` handler, add validation. Replace:

```csharp
        group.MapGet("/", (string slug, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

With:

```csharp
        group.MapGet("/", (string slug, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(slug) || slug.Contains("..") || slug.Contains('/') || slug.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid slug" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
```

- [ ] **Step 6: Guard Hangfire dashboard behind Development environment in `Program.cs`**

Replace:

```csharp
app.UseHangfireDashboard();
```

With:

```csharp
if (app.Environment.IsDevelopment())
    app.UseHangfireDashboard();
```

- [ ] **Step 7: Build and verify**

```bash
cd J:/workspace2/llm/continue_story_4/backend
dotnet build KnowledgeEngine.sln
```

Expected: Build succeeds with no errors.

- [ ] **Step 8: Commit**

```bash
git add backend/src/KnowledgeEngine.Api/Endpoints/ backend/src/KnowledgeEngine.Api/Program.cs
git commit -m "fix(backend): add slug validation, title validation, guard Hangfire dashboard, return 202 for conversion"
```

---

## Task 4: Backend Service Cleanup

**Dependencies:** None (can run in parallel)
**Files:**
- Modify: `backend/src/KnowledgeEngine.Api/Services/IConversionService.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Services/ConversionService.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Services/AgentService.cs`
- Delete: `backend/src/KnowledgeEngine.Api/Services/ILoreService.cs`
- Delete: `backend/src/KnowledgeEngine.Api/Services/LoreService.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Program.cs`

- [ ] **Step 1: Add CancellationToken to `IConversionService.cs`**

Replace the entire file:

```csharp
namespace KnowledgeEngine.Api.Services;

public interface IConversionService
{
    Task<string> ConvertToMarkdownAsync(string inputPath, string outputPath, CancellationToken ct = default);
}
```

- [ ] **Step 2: Pass CancellationToken through in `ConversionService.cs`**

Replace the `ConvertToMarkdownAsync` method. The full file becomes:

```csharp
using System.Diagnostics;

namespace KnowledgeEngine.Api.Services;

public class ConversionService : IConversionService
{
    private readonly ILogger<ConversionService> _logger;

    public ConversionService(ILogger<ConversionService> logger)
    {
        _logger = logger;
    }

    public async Task<string> ConvertToMarkdownAsync(string inputPath, string outputPath, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input file not found: {inputPath}");

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "markitdown",
            Arguments = $"\"{inputPath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Starting conversion: {Input} -> {Output}", inputPath, outputPath);

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            _logger.LogError("markitdown failed (exit {Code}): {Error}", process.ExitCode, error);
            throw new InvalidOperationException($"markitdown conversion failed: {error}");
        }

        await File.WriteAllTextAsync(outputPath, output, ct);

        _logger.LogInformation("Conversion complete: {Output} ({Length} chars)", outputPath, output.Length);
        return outputPath;
    }
}
```

- [ ] **Step 3: Add logging to `AgentService.cs`**

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

    public async Task<string> SendPromptAsync(string message, CancellationToken ct = default)
    {
        _logger.LogInformation("Sending non-streaming prompt to agent ({Length} chars)", message.Length);

        try
        {
            var response = await _http.PostAsync($"{_agentBaseUrl}/api/prompt",
                new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<JsonElement>(json);

            if (result.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
                return data.ToString() ?? "";

            _logger.LogWarning("Agent returned no data for prompt");
            return "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send prompt to agent");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
        _logger.LogInformation("Sending streaming prompt to agent ({Length} chars)", message.Length);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_agentBaseUrl}/api/prompt/stream");
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

- [ ] **Step 4: Delete dead code — `ILoreService.cs` and `LoreService.cs`**

Delete these files:
```bash
rm backend/src/KnowledgeEngine.Api/Services/ILoreService.cs
rm backend/src/KnowledgeEngine.Api/Services/LoreService.cs
```

- [ ] **Step 5: Remove dead registrations from `Program.cs`**

Remove the `ILoreService` registration line. Replace:

```csharp
builder.Services.AddHttpClient<IAgentService, AgentService>();
builder.Services.AddSingleton<ILoreService, LoreService>();
builder.Services.AddTransient<LoreJobService>();
```

With:

```csharp
builder.Services.AddHttpClient<IAgentService, AgentService>();
builder.Services.AddTransient<LoreJobService>();
```

- [ ] **Step 6: Build and verify**

```bash
cd J:/workspace2/llm/continue_story_4/backend
dotnet build KnowledgeEngine.sln
```

Expected: Build succeeds with no errors.

- [ ] **Step 7: Commit**

```bash
git add -A backend/src/KnowledgeEngine.Api/Services/ backend/src/KnowledgeEngine.Api/Program.cs
git commit -m "refactor(backend): add CancellationToken to conversion, add AgentService logging, remove dead LoreService"
```

---

## Task 5: Frontend BookEditor Fix

**Dependencies:** None (can run in parallel)
**Files:**
- Modify: `frontend/src/lib/components/BookEditor.svelte`
- Modify: `frontend/src/routes/books/[slug]/+page.svelte`

The Milkdown theme CSS is never imported, so the editor renders with no visual styling. Additionally, when the parent component passes new `content` (e.g., after initial load), the editor doesn't update. And the `onContentChange` fires on every keystroke with no debounce, causing a save API call per keystroke.

- [ ] **Step 1: Rewrite `frontend/src/lib/components/BookEditor.svelte`**

Replace the entire file:

```svelte
<script lang="ts">
  import { onMount } from 'svelte';
  import { Editor, rootCtx, defaultValueCtx, editorViewCtx } from '@milkdown/kit/core';
  import { commonmark } from '@milkdown/kit/preset/commonmark';
  import { gfm } from '@milkdown/kit/preset/gfm';
  import { nord } from '@milkdown/theme-nord';
  import '@milkdown/theme-nord/lib/style.css';
  import { listener, listenerCtx } from '@milkdown/plugin-listener';

  let { content = $bindable(''), readonly = $bindable(false), onContentChange }: {
    content: string;
    readonly: boolean;
    onContentChange?: (markdown: string) => void;
  } = $props();

  let editorEl: HTMLDivElement;
  let editor: Editor | null = $state(null);
  let lastContent = $state(content);

  onMount(async () => {
    editor = await Editor.make()
      .config((ctx) => {
        ctx.set(rootCtx, editorEl);
        ctx.set(defaultValueCtx, content);
        ctx.set(listenerCtx, {});
      })
      .use(commonmark)
      .use(gfm)
      .use(nord)
      .use(listener)
      .create();

    const listenerManager = editor.ctx.get(listenerCtx);
    listenerManager.markdownUpdated((_ctx, markdown, _prevMarkdown) => {
      if (markdown !== lastContent) {
        lastContent = markdown;
        content = markdown;
        onContentChange?.(markdown);
      }
    });
  });

  // React to external content changes (e.g., parent loads new content)
  $effect(() => {
    if (!editor) return;
    // Only replace content if it changed from outside (not from our own edit)
    if (content !== lastContent) {
      lastContent = content;
      // Replace the entire editor content by destroying and recreating
      // (Milkdown v7 doesn't have a clean setContent API)
      editor.destroy();
      editor = null;
      Editor.make()
        .config((ctx) => {
          ctx.set(rootCtx, editorEl);
          ctx.set(defaultValueCtx, content);
          ctx.set(listenerCtx, {});
        })
        .use(commonmark)
        .use(gfm)
        .use(nord)
        .use(listener)
        .create()
        .then((e) => {
          editor = e;
          const listenerManager = e.ctx.get(listenerCtx);
          listenerManager.markdownUpdated((_ctx, markdown, _prevMarkdown) => {
            if (markdown !== lastContent) {
              lastContent = markdown;
              content = markdown;
              onContentChange?.(markdown);
            }
          });
        });
    }
  });

  // Toggle readonly
  $effect(() => {
    if (!editor) return;
    const editorView = editor.ctx.get(editorViewCtx);
    if (readonly) {
      editorView.dom.setAttribute('contenteditable', 'false');
      editorView.dom.style.opacity = '1';
    } else {
      editorView.dom.setAttribute('contenteditable', 'true');
    }
  });
</script>

<div class="milkdown-wrapper" class:readonly>
  <div bind:this={editorEl}></div>
</div>

<style>
  .milkdown-wrapper {
    width: 100%;
    height: 100%;
    overflow-y: auto;
    padding: 32px;
  }

  .milkdown-wrapper :global(.milkdown) {
    max-width: 800px;
    margin: 0 auto;
    color: var(--text-primary);
    font-size: 16px;
    line-height: 1.7;
  }

  .milkdown-wrapper :global(.milkdown h1) {
    font-size: 2em;
    font-weight: 700;
    margin-bottom: 16px;
    color: var(--text-primary);
    border-bottom: 1px solid var(--border);
    padding-bottom: 8px;
  }

  .milkdown-wrapper :global(.milkdown h2) {
    font-size: 1.5em;
    font-weight: 600;
    margin-top: 24px;
    margin-bottom: 12px;
    color: var(--text-primary);
  }

  .milkdown-wrapper :global(.milkdown p) {
    margin-bottom: 16px;
  }

  .milkdown-wrapper :global(.milkdown code) {
    background: var(--bg-tertiary);
    padding: 2px 6px;
    border-radius: 4px;
    font-size: 0.9em;
  }

  .milkdown-wrapper :global(.milkdown pre) {
    background: var(--bg-tertiary);
    padding: 16px;
    border-radius: 8px;
    overflow-x: auto;
    margin-bottom: 16px;
  }

  .milkdown-wrapper :global(.milkdown blockquote) {
    border-left: 3px solid var(--accent);
    padding-left: 16px;
    margin-left: 0;
    color: var(--text-secondary);
  }

  .milkdown-wrapper :global(.milkdown hr) {
    border: none;
    border-top: 1px solid var(--border);
    margin: 24px 0;
  }

  .milkdown-wrapper.readonly :global(.milkdown) {
    pointer-events: none;
  }
</style>
```

Key changes:
1. `import '@milkdown/theme-nord/lib/style.css'` — adds theme styling
2. `$effect` on `content` — recreates editor when content changes externally
3. `lastContent` tracking — prevents infinite loops between editor updates and prop changes

- [ ] **Step 2: Add debounced save to `frontend/src/routes/books/[slug]/+page.svelte`**

Add a debounce utility after the existing state declarations. Find:

```typescript
  let showChat = $state(false);
  let showLore = $state(false);
```

Insert after:

```typescript
  let saveTimeout: ReturnType<typeof setTimeout> | null = null;

  async function debouncedSave(newContent: string) {
    if (saveTimeout) clearTimeout(saveTimeout);
    saveTimeout = setTimeout(async () => {
      await saveContent(newContent);
    }, 1000);
  }
```

Then update the `onContentChange` handler in the template. Find:

```svelte
          <BookEditor
            bind:content
            readonly={!isEditing}
            onContentChange={(md) => { if (isEditing) saveContent(md); }}
          />
```

Replace with:

```svelte
          <BookEditor
            bind:content
            readonly={!isEditing}
            onContentChange={(md) => { if (isEditing) debouncedSave(md); }}
          />
```

- [ ] **Step 3: Build and verify**

```bash
cd J:/workspace2/llm/continue_story_4/frontend
npx svelte-check --tsconfig ./tsconfig.json 2>&1 | tail -5
```

Expected: No errors.

- [ ] **Step 4: Commit**

```bash
git add frontend/src/lib/components/BookEditor.svelte frontend/src/routes/books/
git commit -m "fix(frontend): import Milkdown theme CSS, add content reactivity, debounce editor save"
```

---

## Task 6: Frontend API + Chat SSE Fix

**Dependencies:** None (can run in parallel)
**Files:**
- Modify: `frontend/src/lib/api.ts`

The SSE parser splits on `\n` but a single `read()` chunk can contain partial SSE messages or multiple messages. Data that spans chunk boundaries is lost. Also, non-200 HTTP responses from the chat endpoint are not handled — a 500 error silently fails.

- [ ] **Step 1: Fix SSE parsing and add error handling in `frontend/src/lib/api.ts`**

Replace the `chat` method in the `api` object. Find the entire `chat(` method starting at:

```typescript
  // Chat (SSE)
  chat(bookSlug: string, message: string, onChunk: (data: string) => void, onDone: () => void): AbortController {
```

And replace through the closing of the method (ends with `},` before the closing `};` of `api`):

```typescript
  // Chat (SSE)
  chat(bookSlug: string, message: string, onChunk: (data: string) => void, onDone: () => void, onError?: (err: string) => void): AbortController {
    const controller = new AbortController();
    fetch(`${BASE}/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bookSlug, message }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) {
          const text = await res.text().catch(() => res.statusText);
          onError?.(text || `HTTP ${res.status}`);
          onDone();
          return;
        }
        const reader = res.body?.getReader();
        if (!reader) { onDone(); return; }
        const decoder = new TextDecoder();
        let sseBuffer = '';
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          sseBuffer += decoder.decode(value, { stream: true });
          // Process complete SSE messages (delimited by blank line)
          let boundary: number;
          while ((boundary = sseBuffer.indexOf('\n\n')) !== -1) {
            const message = sseBuffer.slice(0, boundary);
            sseBuffer = sseBuffer.slice(boundary + 2);
            // Parse the SSE message (may contain multiple data: lines)
            for (const line of message.split('\n')) {
              if (line.startsWith('data: ')) {
                try {
                  const evt = JSON.parse(line.slice(6));
                  if (evt.type === 'agent_end') {
                    onDone();
                    return;
                  } else if (evt.type === 'message_update') {
                    const delta = evt.assistantMessageEvent;
                    if (delta?.type === 'text_delta') {
                      onChunk(delta.delta);
                    }
                  } else if (evt.type === 'error') {
                    onError?.(evt.message || 'Unknown error');
                  }
                } catch {
                  // Ignore parse errors
                }
              }
            }
          }
        }
        onDone();
      })
      .catch((err) => {
        if (err.name !== 'AbortError') {
          console.error('Chat error:', err);
          onError?.(err.message);
        }
        onDone();
      });
    return controller;
  },
```

Key changes:
1. **Buffer until `\n\n`** — properly handles chunk boundaries
2. **`res.ok` check** — handles HTTP errors before reading stream
3. **`onError` callback** — callers can display errors
4. **`error` event type handling** — catches agent-side errors

- [ ] **Step 2: Commit**

```bash
git add frontend/src/lib/api.ts
git commit -m "fix(frontend): proper SSE chunk-boundary parsing, add HTTP error and error event handling"
```

---

## Task 7: Frontend Panel UX Fixes

**Dependencies:** Task 6 (api.ts onError callback used by ChatPanel)
**Files:**
- Modify: `frontend/src/lib/components/ChatPanel.svelte`
- Modify: `frontend/src/lib/components/LorePanel.svelte`
- Modify: `frontend/src/routes/+page.svelte`

- [ ] **Step 1: Fix ChatPanel — move $effect to top level, add error display**

Replace the entire `frontend/src/lib/components/ChatPanel.svelte`:

```svelte
<script lang="ts">
  import { api } from '$lib/api';

  let { slug }: { slug: string } = $props();

  let messages: Array<{ role: 'user' | 'assistant'; text: string }> = $state([]);
  let input = $state('');
  let streaming = $state(false);
  let currentResponse = $state('');
  let chatError = $state('');
  let chatContainer: HTMLDivElement;

  // Top-level effect for auto-scroll — reacts to messages and streaming response
  $effect(() => {
    // Touch both reactive values to subscribe
    const _msgs = messages;
    const _resp = currentResponse;
    if (chatContainer) {
      chatContainer.scrollTop = chatContainer.scrollHeight;
    }
  });

  async function send() {
    const msg = input.trim();
    if (!msg || streaming) return;

    chatError = '';
    messages = [...messages, { role: 'user', text: msg }];
    input = '';
    streaming = true;
    currentResponse = '';

    api.chat(
      slug,
      msg,
      (chunk) => {
        currentResponse += chunk;
      },
      () => {
        if (currentResponse) {
          messages = [...messages, { role: 'assistant', text: currentResponse }];
        }
        currentResponse = '';
        streaming = false;
      },
      (err) => {
        chatError = err;
      }
    );
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }
</script>

<div class="chat-panel">
  <h3 class="panel-title">AI Chat</h3>

  <div class="messages" bind:this={chatContainer}>
    {#if messages.length === 0 && !streaming}
      <p class="empty-hint">Ask a question about this book...</p>
    {/if}

    {#each messages as msg}
      <div class="message" class:user={msg.role === 'user'} class:assistant={msg.role === 'assistant'}>
        <div class="message-role">{msg.role === 'user' ? 'You' : 'AI'}</div>
        <div class="message-text">{msg.text}</div>
      </div>
    {/each}

    {#if streaming && currentResponse}
      <div class="message assistant">
        <div class="message-role">AI</div>
        <div class="message-text">{currentResponse}<span class="cursor">|</span></div>
      </div>
    {/if}

    {#if chatError}
      <div class="chat-error">{chatError}</div>
    {/if}
  </div>

  <form class="input-form" onsubmit={(e) => { e.preventDefault(); send(); }}>
    <textarea
      bind:value={input}
      placeholder="Ask about the book..."
      disabled={streaming}
      onkeydown={handleKeydown}
      rows="2"
    ></textarea>
    <button type="submit" class="btn" disabled={streaming || !input.trim()}>
      Send
    </button>
  </form>
</div>

<style>
  .chat-panel {
    display: flex;
    flex-direction: column;
    height: 100%;
  }

  .panel-title {
    padding: 12px 16px;
    font-size: 14px;
    font-weight: 600;
    border-bottom: 1px solid var(--border);
  }

  .messages {
    flex: 1;
    overflow-y: auto;
    padding: 12px 16px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .empty-hint {
    color: var(--text-secondary);
    font-size: 13px;
    text-align: center;
    padding-top: 24px;
  }

  .message {
    padding: 8px 12px;
    border-radius: 8px;
    font-size: 13px;
    line-height: 1.5;
  }

  .message.user {
    background: var(--bg-tertiary);
    margin-left: 32px;
  }

  .message.assistant {
    background: #1a2332;
    margin-right: 32px;
  }

  .message-role {
    font-size: 11px;
    font-weight: 600;
    color: var(--text-secondary);
    margin-bottom: 4px;
    text-transform: uppercase;
  }

  .message-text {
    white-space: pre-wrap;
    word-break: break-word;
  }

  .chat-error {
    background: #3d1f1f;
    color: #f97583;
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 12px;
  }

  .cursor {
    animation: blink 1s step-end infinite;
  }

  @keyframes blink {
    50% { opacity: 0; }
  }

  .input-form {
    display: flex;
    gap: 8px;
    padding: 12px 16px;
    border-top: 1px solid var(--border);
  }

  .input-form textarea {
    flex: 1;
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 13px;
    resize: none;
    font-family: inherit;
  }

  .btn {
    padding: 6px 14px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 13px;
  }

  .btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
```

Key changes:
1. **`$effect` moved to top level** — no longer creates a new effect per `send()` call
2. **`chatError` state + error display** — shows errors to user
3. **`onError` callback** passed to `api.chat()`

- [ ] **Step 2: Fix LorePanel — add polling timeout**

Replace the `generate` function in `frontend/src/lib/components/LorePanel.svelte`. Find:

```typescript
  async function generate() {
    generating = true;
    try {
      await api.triggerLoreGeneration(slug);
      const interval = setInterval(async () => {
        await loadFiles();
        if (files.length > 0) {
          clearInterval(interval);
          generating = false;
          await loadFile(files[0]);
        }
      }, 5000);
    } catch (err) {
      console.error('Lore generation failed:', err);
      generating = false;
    }
  }
```

Replace with:

```typescript
  let loreError = $state('');

  async function generate() {
    generating = true;
    loreError = '';
    try {
      await api.triggerLoreGeneration(slug);
      let attempts = 0;
      const maxAttempts = 24; // 24 * 5s = 2 min timeout
      const interval = setInterval(async () => {
        attempts++;
        await loadFiles();
        if (files.length > 0) {
          clearInterval(interval);
          generating = false;
          await loadFile(files[0]);
        } else if (attempts >= maxAttempts) {
          clearInterval(interval);
          generating = false;
          loreError = 'Lore generation timed out. The agent may not have API keys configured.';
        }
      }, 5000);
    } catch (err: any) {
      loreError = err.message || 'Lore generation failed';
      generating = false;
    }
  }
```

Also add error display in the template. Find:

```svelte
    {:else if files.length === 0}
      <p class="empty-hint">No lore generated yet. Click "Generate Lore" to analyze the book.</p>
    {/if}
```

Replace with:

```svelte
    {:else if files.length === 0}
      <p class="empty-hint">No lore generated yet. Click "Generate Lore" to analyze the book.</p>
    {/if}

    {#if loreError}
      <div class="lore-error">{loreError}</div>
    {/if}
```

Add the error style in the `<style>` block. Before the closing `</style>`, add:

```css
  .lore-error {
    background: #3d1f1f;
    color: #f97583;
    padding: 8px 12px;
    border-radius: 6px;
    font-size: 12px;
    margin-top: 12px;
  }
```

- [ ] **Step 3: Add error display and delete button to `frontend/src/routes/+page.svelte`**

Add an error state variable after `let newAuthor = $state('');`:

```typescript
  let createError = $state('');
```

Update the `createBook` function to show errors. Find:

```typescript
    } catch (err) {
      console.error('Failed to create book:', err);
    }
```

Replace with:

```typescript
    } catch (err: any) {
      createError = err.message || 'Failed to create book';
    }
```

Add error display in the template. Find:

```svelte
    {/if}
```

(The one right after the create form's closing `</form>`, before the `{#if loading}` block.)

Insert after the `</form>` tag's closing, before the `{/if}`:

```svelte
      {#if createError}
        <div class="create-error">{createError}</div>
      {/if}
```

Wait, that's inside the `{#if showCreateForm}` block. Actually, put it after the `{/if}` of showCreateForm. Find:

```svelte
    {/if}

    {#if loading}
```

Replace with:

```svelte
    {/if}

    {#if createError}
      <div class="create-error">{createError}</div>
    {/if}

    {#if loading}
```

Add the error style. Before the closing `</style>`, add:

```css
  .create-error {
    padding: 8px 16px;
    background: #3d1f1f;
    color: #f97583;
    font-size: 13px;
  }
```

- [ ] **Step 4: Commit**

```bash
git add frontend/src/lib/components/ChatPanel.svelte frontend/src/lib/components/LorePanel.svelte frontend/src/routes/+page.svelte
git commit -m "fix(frontend): ChatPanel top-level effect + error display, LorePanel polling timeout, create error feedback"
```

---

## Task 8: End-to-End Verification

**Dependencies:** Tasks 1–7 all complete
**Files:** None (read-only verification)

- [ ] **Step 1: Clean build all containers**

```bash
cd J:/workspace2/llm/continue_story_4
docker compose down -v 2>/dev/null || true
docker compose build 2>&1
```

Expected: All 3 services build successfully.

- [ ] **Step 2: Start the stack**

```bash
docker compose up -d 2>&1
```

- [ ] **Step 3: Wait and verify health**

```bash
sleep 20
curl -f http://localhost:5000/api/health
```

Expected: `{"status":"healthy",...}`

- [ ] **Step 4: Run the full book lifecycle**

```bash
# 1. Create a book
echo "=== Create ==="
curl -s -X POST http://localhost:5000/api/books \
  -H "Content-Type: application/json" \
  -d '{"title":"Test Book","author":"Test Author"}'

# 2. Seed content
echo ""
echo "=== Seed ==="
docker compose exec api sh -c 'mkdir -p /library/test-book && echo "# Test\n\nHello world." > /library/test-book/book.md'

# 3. Read content
echo ""
echo "=== Read ==="
curl -s http://localhost:5000/api/books/test-book/content

# 4. Update content
echo ""
echo "=== Update ==="
curl -s -X PUT http://localhost:5000/api/books/test-book/content \
  -H "Content-Type: application/json" \
  -d '{"content":"# Updated\n\nNew content."}'

# 5. Verify update
echo ""
echo "=== Verify ==="
curl -s http://localhost:5000/api/books/test-book/content

# 6. Test slug validation (path traversal)
echo ""
echo "=== Security: path traversal ==="
curl -s -w "\nHTTP Status: %{http_code}" http://localhost:5000/api/books/../etc/content

# 7. Test empty title validation
echo ""
echo "=== Validation: empty title ==="
curl -s -w "\nHTTP Status: %{http_code}" -X POST http://localhost:5000/api/books \
  -H "Content-Type: application/json" \
  -d '{"title":""}'

# 8. List lore
echo ""
echo "=== Lore ==="
curl -s http://localhost:5000/api/books/test-book/lore

# 9. List all books
echo ""
echo "=== List ==="
curl -s http://localhost:5000/api/books

# 10. Delete book
echo ""
echo "=== Delete ==="
curl -s -w "\nHTTP Status: %{http_code}" -X DELETE http://localhost:5000/api/books/test-book

# 11. Verify deletion
echo ""
echo "=== Verify delete ==="
curl -s http://localhost:5000/api/books
```

Expected:
- Create returns 201
- Read/Update work correctly
- Path traversal returns 400
- Empty title returns 400
- Delete returns 204
- Final list is empty

- [ ] **Step 5: Verify frontend loads**

```bash
curl -f http://localhost:5173 | head -5
```

Expected: HTML page with SvelteKit app shell.

- [ ] **Step 6: Verify agent is healthy**

```bash
docker compose ps
```

Expected: `agent` container shows `(healthy)` status.

- [ ] **Step 7: Stop containers**

```bash
docker compose down -v
```

- [ ] **Step 8: Final commit if any pending changes**

```bash
git status --short
```

Expected: No uncommitted changes.

- [ ] **Step 9: Verify plan success criteria**

Check each fix:
- [ ] **Agent starts:** Container runs, agent process spawns via `npx tsx`
- [ ] **Non-streaming prompt works:** `/api/prompt` collects response from events
- [ ] **SSE streaming fixed:** Callback registered before prompt sent, chunk boundaries handled
- [ ] **Graceful shutdown:** Agent child process killed on SIGTERM
- [ ] **Restart limit:** Agent stops after 5 failed restarts
- [ ] **Slug validation:** Path traversal (`../`) returns 400
- [ ] **Title validation:** Empty title returns 400
- [ ] **Slug collision:** Concurrent creation returns 409, not 500
- [ ] **Hangfire dashboard:** Only available in Development environment
- [ ] **Conversion returns 202:** `POST /api/books/{slug}/convert` returns Accepted
- [ ] **Dead code removed:** `ILoreService`/`LoreService` deleted
- [ ] **AgentService logging:** Logs prompt sends and failures
- [ ] **ConversionService CancellationToken:** Passes CT through to process/IO
- [ ] **Milkdown theme CSS:** `@milkdown/theme-nord/lib/style.css` imported
- [ ] **Editor content reactivity:** Editor updates when parent changes `content` prop
- [ ] **Debounced save:** 1-second debounce on editor content change
- [ ] **SSE chunk parsing:** Buffer-based parsing handles chunk boundaries
- [ ] **Chat HTTP error handling:** Non-200 responses displayed to user
- [ ] **ChatPanel $effect:** Effect at top level, not inside send()
- [ ] **LorePanel timeout:** Polling stops after 2 minutes
- [ ] **Create error display:** Failed book creation shows error message
