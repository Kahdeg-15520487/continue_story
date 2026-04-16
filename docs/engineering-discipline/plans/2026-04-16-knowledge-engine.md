# Knowledge Engine Implementation Plan

> **Worker note:** Execute this plan task-by-task using the agentic-run-plan skill or subagents. Each step uses checkbox (`- [ ]`) syntax for progress tracking.

**Goal:** Build a $0-cost, high-extensibility "Knowledge Engine" — a system that converts documents (EPUB, PDF, DOCX, etc.) into Markdown, stores them as folder-based "books," provides an AI-powered editor (Milkdown) for reading/editing, and uses the Pi Coding Agent SDK in RPC mode as an intelligent sidecar for lore extraction, Q&A, and headless editing.

**Architecture:** Dockerized 4-container system — .NET 9 API backend with Hangfire job queue and SQLite, SvelteKit frontend with Milkdown editor, Pi Agent sidecar in RPC mode, and a shared library volume. The backend manages library operations and delegates all LLM intelligence to the Pi Agent via JSON-RPC over stdin/stdout. The frontend communicates with the backend via REST/SSE.

**Tech Stack:**
- **Backend:** .NET 9 SDK (latest), ASP.NET Core Minimal APIs, Hangfire, SQLite, Microsoft.Extensions.AI
- **Frontend:** SvelteKit, Milkdown v7+, Vercel AI SDK (Svelte)
- **Intelligence:** `@mariozechner/pi-coding-agent` in RPC mode (uses Pi SDK's built-in provider/model system)
- **Conversion:** Python `markitdown` CLI (Microsoft, MIT License)
- **Infrastructure:** Docker Compose, SQLite (volume-mounted)

**Work Scope:**
- **In scope:** Book-as-folder library management, file conversion pipeline (MarkItDown CLI), Milkdown editor with readonly/read-write toggle, Pi Agent RPC integration for lore generation and chat, Hangfire background job processing, SSE streaming from backend to frontend, Docker Compose orchestration, Pi Agent "lore" skill
- **Out of scope:** User authentication, OAuth, file upload from browser, full-text search indexing, mobile responsive design, deployment to cloud, batch import UI

---

**Verification Strategy:**
- **Level:** build-only (greenfield project — no test infrastructure exists yet)
- **Command:** `docker compose build && docker compose up -d && curl -f http://localhost:5000/api/health`
- **What it validates:** All containers build and start; backend health endpoint responds

---

## File Structure Mapping

```
/
├── docker-compose.yml                    # Orchestration: api, frontend, agent
├── .env.example                          # Environment variable template
├── .gitignore
│
├── backend/                              # .NET 9 Web API
│   ├── Dockerfile
│   ├── KnowledgeEngine.sln
│   └── src/
│       └── KnowledgeEngine.Api/
│           ├── KnowledgeEngine.Api.csproj
│           ├── Program.cs                 # Minimal API setup, DI, middleware
│           ├── appsettings.json
│           ├── appsettings.Development.json
│           ├── Models/
│           │   ├── Book.cs               # Book metadata entity
│           │   ├── ConversionJob.cs      # Hangfire job model
│           │   └── ChatRequest.cs        # Chat request DTO
│           ├── Services/
│           │   ├── ILibraryService.cs    # Library management interface
│           │   ├── LibraryService.cs     # Folder CRUD, listing
│           │   ├── IConversionService.cs # File conversion interface
│           │   ├── ConversionService.cs  # MarkItDown CLI wrapper
│           │   ├── IAgentService.cs      # Pi Agent RPC interface
│           │   ├── AgentService.cs       # RPC client (spawn, send, stream)
│           │   └── ILoreService.cs       # Lore extraction interface
│           │   └── LoreService.cs        # Triggers agent for lore generation
│           ├── Endpoints/
│           │   ├── LibraryEndpoints.cs   # GET/POST /api/books, GET /api/books/{slug}
│           │   ├── EditorEndpoints.cs    # GET/PUT /api/books/{slug}/content
│           │   ├── ConversionEndpoints.cs# POST /api/books/convert
│           │   ├── ChatEndpoints.cs      # POST /api/chat (SSE streaming)
│           │   └── LoreEndpoints.cs      # GET/POST /api/books/{slug}/lore
│           ├── Data/
│           │   └── AppDbContext.cs       # EF Core SQLite context for metadata
│           └── Migrations/               # EF Core migrations
│
├── frontend/                             # SvelteKit app
│   ├── Dockerfile
│   ├── package.json
│   ├── svelte.config.js
│   ├── vite.config.ts
│   ├── src/
│   │   ├── app.html
│   │   ├── app.d.ts
│   │   ├── lib/
│   │   │   ├── api.ts                   # Backend API client (fetch wrapper)
│   │   │   ├── components/
│   │   │   │   ├── BookList.svelte       # Library sidebar
│   │   │   │   ├── BookEditor.svelte     # Milkdown editor wrapper
│   │   │   │   ├── ChatPanel.svelte      # AI chat sidebar
│   │   │   │   └── LorePanel.svelte      # Wiki/lore viewer
│   │   │   └── types.ts                 # Shared TypeScript types
│   │   └── routes/
│   │       ├── +layout.svelte           # App shell (sidebar + editor)
│   │       ├── +page.svelte             # Home / library view
│   │       └── books/
│   │           └── [slug]/
│   │               └── +page.svelte     # Book editor view
│   └── static/
│       └── favicon.png
│
├── agent/                                # Pi Agent RPC sidecar
│   ├── Dockerfile
│   ├── package.json
│   ├── tsconfig.json
│   └── src/
│       └── index.ts                     # RPC bridge: stdin/stdout passthrough
│
├── skills/                               # Pi Agent skills (mounted into agent container)
│   └── lore-extraction/
│       └── SKILL.md                     # Instructions for lore generation from markdown books
│
└── library/                              # Book storage volume (docker-compose mount)
    └── .gitkeep
```

---

## Phase 1: Foundation (Docker + Backend Skeleton + Agent Sidecar)

### Task 1: Project Scaffolding and Docker Compose

**Dependencies:** None (can run in parallel with Task 2)
**Files:**
- Create: `docker-compose.yml`
- Create: `.env.example`
- Create: `.gitignore`
- Create: `backend/Dockerfile`
- Create: `frontend/Dockerfile`
- Create: `agent/Dockerfile`
- Create: `library/.gitkeep`

- [ ] **Step 1: Create `.gitignore`**

```gitignore
# .NET
bin/
obj/
*.user
*.suo

# Node
node_modules/
dist/
build/
.svelte-kit/

# IDE
.vscode/
.idea/

# Environment
.env

# OS
.DS_Store
Thumbs.db

# SQLite
*.db
*.db-journal

# Library (user data)
library/*
!library/.gitkeep
```

- [ ] **Step 2: Create `.env.example`**

```env
# Pi Agent — uses its built-in provider/model system.
# Set API keys for whichever provider you configure in Pi.
# These are passed through to the agent container.
ANTHROPIC_API_KEY=
GOOGLE_API_KEY=
OPENAI_API_KEY=

# Backend
ASPNETCORE_ENVIRONMENT=Development
LIBRARY_PATH=/library

# Frontend
PUBLIC_API_BASE=http://localhost:5000
```

- [ ] **Step 3: Create `docker-compose.yml`**

```yaml
services:
  api:
    build:
      context: ./backend
      dockerfile: Dockerfile
    ports:
      - "5000:5000"
    volumes:
      - library-data:/library
      - sqlite-data:/data
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - LIBRARY_PATH=/library
      - SQLITE_PATH=/data/knowledge-engine.db
      - AGENT_HOST=agent
      - AGENT_PORT=3001
    depends_on:
      - agent

  frontend:
    build:
      context: ./frontend
      dockerfile: Dockerfile
    ports:
      - "5173:5173"
    environment:
      - PUBLIC_API_BASE=http://localhost:5000
    depends_on:
      - api

  agent:
    build:
      context: ./agent
      dockerfile: Dockerfile
    volumes:
      - library-data:/library:ro
      - ./skills:/skills:ro
    environment:
      - PI_CWD=/library
    # No port mapping needed — api talks to agent internally

volumes:
  library-data:
  sqlite-data:
```

- [ ] **Step 4: Create `backend/Dockerfile`**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore KnowledgeEngine.sln
RUN dotnet publish KnowledgeEngine.sln -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "KnowledgeEngine.Api.dll"]
```

- [ ] **Step 5: Create `frontend/Dockerfile`**

```dockerfile
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm install
COPY . .
RUN npm run build

FROM node:22-alpine AS dev
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm install
COPY . .
EXPOSE 5173
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]
```

- [ ] **Step 6: Create `agent/Dockerfile`**

```dockerfile
FROM node:22-alpine
WORKDIR /app
RUN npm install -g @mariozechner/pi-coding-agent
COPY src/ ./src/
COPY package.json ./
RUN npm install
EXPOSE 3001
CMD ["node", "src/index.ts"]
```

- [ ] **Step 7: Create `library/.gitkeep`**

Empty file to preserve the directory in git.

- [ ] **Step 8: Initialize git repo and commit**

```bash
git init
git add -A
git commit -m "chore: project scaffolding with docker compose"
```

---

### Task 2: .NET 9 Backend Skeleton

**Dependencies:** None (can run in parallel with Task 1)
**Files:**
- Create: `backend/KnowledgeEngine.sln`
- Create: `backend/src/KnowledgeEngine.Api/KnowledgeEngine.Api.csproj`
- Create: `backend/src/KnowledgeEngine.Api/Program.cs`
- Create: `backend/src/KnowledgeEngine.Api/appsettings.json`
- Create: `backend/src/KnowledgeEngine.Api/appsettings.Development.json`
- Create: `backend/src/KnowledgeEngine.Api/Models/Book.cs`
- Create: `backend/src/KnowledgeEngine.Api/Models/ConversionJob.cs`
- Create: `backend/src/KnowledgeEngine.Api/Models/ChatRequest.cs`
- Create: `backend/src/KnowledgeEngine.Api/Data/AppDbContext.cs`
- Create: `backend/src/KnowledgeEngine.Api/Endpoints/LibraryEndpoints.cs`

- [ ] **Step 1: Create the .NET solution and project**

```bash
cd backend
dotnet new sln -n KnowledgeEngine
dotnet new webapi -n KnowledgeEngine.Api -o src/KnowledgeEngine.Api --no-https
dotnet sln KnowledgeEngine.sln add src/KnowledgeEngine.Api/KnowledgeEngine.Api.csproj
cd src/KnowledgeEngine.Api
dotnet add package Microsoft.EntityFrameworkCore.Sqlite
dotnet add package Microsoft.EntityFrameworkCore.Design
dotnet add package Hangfire.Core
dotnet add package Hangfire.Sqlite
```

- [ ] **Step 2: Create `Models/Book.cs`**

```csharp
namespace KnowledgeEngine.Api.Models;

public class Book
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";        // URL-safe folder name
    public string Title { get; set; } = "";
    public string? Author { get; set; }
    public int? Year { get; set; }
    public string? SourceFile { get; set; }        // Original uploaded filename
    public string Status { get; set; } = "pending"; // pending, converting, ready, error
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 3: Create `Models/ConversionJob.cs`**

```csharp
namespace KnowledgeEngine.Api.Models;

public class ConversionJob
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued, processing, completed, failed
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
```

- [ ] **Step 4: Create `Models/ChatRequest.cs`**

```csharp
namespace KnowledgeEngine.Api.Models;

public class ChatRequest
{
    public string BookSlug { get; set; } = "";
    public string Message { get; set; } = "";
}
```

- [ ] **Step 5: Create `Data/AppDbContext.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Models;

namespace KnowledgeEngine.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<ConversionJob> ConversionJobs => Set<ConversionJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Book>(b =>
        {
            b.HasIndex(x => x.Slug).IsUnique();
        });
    }
}
```

- [ ] **Step 6: Create `appsettings.json`**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "ConnectionStrings": {
    "Default": "Data Source=/data/knowledge-engine.db"
  },
  "Library": {
    "Path": "/library"
  },
  "Agent": {
    "Host": "agent",
    "Port": 3001
  },
  "AllowedHosts": "*"
}
```

- [ ] **Step 7: Create `appsettings.Development.json`**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Information"
    }
  }
}
```

- [ ] **Step 8: Create `Program.cs` with minimal API, DI, Hangfire, and health endpoint**

```csharp
using Hangfire;
using Microsoft.EntityFrameworkCore;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// Hangfire with SQLite
builder.Services.AddHangfire(config => config
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqliteStorage(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHangfireServer();

// CORS (allow frontend in dev)
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
        policy.WithOrigins("http://localhost:5173")
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();

// Auto-migrate
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

app.UseCors("Frontend");

// Health check
app.MapGet("/api/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Register endpoint groups
LibraryEndpoints.Map(app);

// Hangfire dashboard (dev only)
app.UseHangfireDashboard();

app.Run();
```

- [ ] **Step 9: Create `Endpoints/LibraryEndpoints.cs` with GET /api/books and POST /api/books**

```csharp
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class LibraryEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books");

        // List all books
        group.MapGet("/", async (AppDbContext db) =>
        {
            var books = await db.Books
                .OrderBy(b => b.Title)
                .Select(b => new BookSummaryDto(b))
                .ToListAsync();
            return Results.Ok(books);
        });

        // Get single book by slug
        group.MapGet("/{slug}", async (string slug, AppDbContext db) =>
        {
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            return book is null ? Results.NotFound() : Results.Ok(new BookDetailDto(book));
        });

        // Create a new book entry (folder will be created by conversion job)
        group.MapPost("/", async (CreateBookRequest req, AppDbContext db, IConfiguration config) =>
        {
            var slug = GenerateSlug(req.Title);
            if (await db.Books.AnyAsync(b => b.Slug == slug))
                return Results.Conflict(new { error = "Book already exists" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            Directory.CreateDirectory(bookDir);

            var book = new Book
            {
                Slug = slug,
                Title = req.Title,
                Author = req.Author,
                Year = req.Year,
                SourceFile = req.SourceFile,
                Status = "pending",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            db.Books.Add(book);
            await db.SaveChangesAsync();

            return Results.Created($"/api/books/{slug}", new BookDetailDto(book));
        });

        // Delete a book
        group.MapDelete("/{slug}", async (string slug, AppDbContext db, IConfiguration config) =>
        {
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null) return Results.NotFound();

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            if (Directory.Exists(bookDir))
                Directory.Delete(bookDir, recursive: true);

            db.Books.Remove(book);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });
    }

    private static string GenerateSlug(string title)
    {
        return string.Join('-', title
            .ToLowerInvariant()
            .Split(' ', '_', '/', '\\')
            .Where(s => s.Length > 0)
            .Select(s => System.Text.RegularExpressions.Regex.Replace(s, "[^a-z0-9]", "")))
            .Where(s => s.Length > 0);
    }
}

// DTOs
public record BookSummaryDto(int Id, string Slug, string Title, string? Author, int? Year, string Status, DateTime UpdatedAt)
{
    public BookSummaryDto(Book b) : this(b.Id, b.Slug, b.Title, b.Author, b.Year, b.Status, b.UpdatedAt) { }
}

public record BookDetailDto(int Id, string Slug, string Title, string? Author, int? Year, string? SourceFile, string Status, string? ErrorMessage, DateTime CreatedAt, DateTime UpdatedAt)
{
    public BookDetailDto(Book b) : this(b.Id, b.Slug, b.Title, b.Author, b.Year, b.SourceFile, b.Status, b.ErrorMessage, b.CreatedAt, b.UpdatedAt) { }
}

public record CreateBookRequest(string Title, string? Author, int? Year, string? SourceFile);
```

- [ ] **Step 10: Add EF Core initial migration**

```bash
cd backend/src/KnowledgeEngine.Api
dotnet ef migrations add InitialCreate
```

- [ ] **Step 11: Verify the backend builds**

```bash
cd backend
dotnet build KnowledgeEngine.sln
```

Expected: Build succeeded with 0 errors.

- [ ] **Step 12: Commit**

```bash
git add backend/
git commit -m "feat(backend): .NET 9 API skeleton with EF Core, Hangfire, and library endpoints"
```

---

### Task 3: Pi Agent RPC Sidecar

**Dependencies:** None (can run in parallel with Tasks 1 and 2)
**Files:**
- Create: `agent/package.json`
- Create: `agent/tsconfig.json`
- Create: `agent/src/index.ts`
- Create: `skills/lore-extraction/SKILL.md`

- [ ] **Step 1: Create `agent/package.json`**

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
    "@mariozechner/pi-coding-agent": "latest"
  },
  "devDependencies": {
    "tsx": "^4.0.0",
    "@types/node": "^22.0.0"
  }
}
```

- [ ] **Step 2: Create `agent/tsconfig.json`**

```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "moduleResolution": "bundler",
    "esModuleInterop": true,
    "strict": true,
    "outDir": "dist",
    "rootDir": "src",
    "skipLibCheck": true
  },
  "include": ["src"]
}
```

- [ ] **Step 3: Create `agent/src/index.ts` — HTTP bridge that wraps Pi Agent RPC**

The .NET backend talks to the agent container over HTTP. This bridge spawns a Pi Agent in RPC mode and exposes a simple HTTP API that the .NET backend calls.

```typescript
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
  // Handle responses (have an id)
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

  // Handle streaming events — forward to any SSE subscribers
  // (handled via event emitter pattern below)
  for (const callback of eventCallbacks.values()) {
    callback(msg);
  }
}

// Simple event bus for SSE subscribers
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

// HTTP API for the .NET backend
async function handleRequest(req: IncomingMessage, res: ServerResponse) {
  const url = new URL(req.url || "/", `http://localhost:${PORT}`);

  // CORS preflight
  if (req.method === "OPTIONS") {
    res.writeHead(200, corsHeaders());
    res.end();
    return;
  }

  // Health check
  if (url.pathname === "/health") {
    res.writeHead(200, { "Content-Type": "application/json", ...corsHeaders() });
    res.end(JSON.stringify({ status: "healthy", agentPid: agentProcess?.pid }));
    return;
  }

  // Send a prompt to the agent (non-streaming)
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

  // SSE endpoint for streaming prompts
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
    eventCallbacks.set(clientId, callback);

    // Send prompt to agent
    try {
      await sendToAgent({ type: "prompt", message });
    } catch (err: any) {
      res.write(`data: ${JSON.stringify({ type: "error", message: err.message })}\n\n`);
    }

    // Clean up after agent finishes (agent_end event)
    const cleanup = setTimeout(() => {
      eventCallbacks.delete(clientId);
      res.end();
    }, 10000); // 10s after last event

    // Override callback to reset timeout on each event
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

  sendError(res, 404, "Not found");
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

// Start
agentProcess = startAgent();

const server = createServer(handleRequest);
server.listen(PORT, () => {
  console.log(`[bridge] HTTP server listening on port ${PORT}`);
});
```

- [ ] **Step 4: Create `skills/lore-extraction/SKILL.md`**

````markdown
---
name: lore-extraction
description: Extracts structured lore data (characters, locations, themes, plot summary) from markdown book files. Use when asked to analyze a book's content and generate wiki-style reference pages.
---

# Lore Extraction Skill

You are a literary analysis assistant. Given a markdown book file, you extract structured lore data and write it to wiki files.

## Workflow

When asked to extract lore from a book:

1. **Read the book file** specified in the request (e.g., `/library/{slug}/book.md`)
2. **Analyze the content** for:
   - **Characters:** Name, role (protagonist/antagonist/supporting), description, key relationships
   - **Locations:** Name, description, significance to the plot
   - **Themes:** Major themes with brief explanation and supporting evidence
   - **Plot Summary:** A concise summary of the entire book
3. **Write the wiki files** to `/library/{slug}/wiki/`:
   - `characters.md` — Structured character list
   - `locations.md` — Structured location list
   - `themes.md` — Thematic analysis
   - `summary.md` — Plot summary

## Output Format

For each wiki file, use this structure:

```markdown
# [Title]

> Auto-generated by Knowledge Engine on [date]

## [Section Name]

### [Item Name]
- **Role:** [description]
- **Description:** [detailed description]
```

## Rules

- Only extract information explicitly present in the text
- If the book is very long, prioritize main characters and key locations
- Use markdown formatting for structure (headers, lists, bold, blockquotes)
- Always create the `/wiki/` directory if it doesn't exist
- If a wiki file already exists, overwrite it with fresh analysis
````

- [ ] **Step 5: Install dependencies and verify**

```bash
cd agent
npm install
```

- [ ] **Step 6: Commit**

```bash
git add agent/ skills/
git commit -m "feat(agent): Pi Agent RPC sidecar with HTTP bridge and lore extraction skill"
```

---

### Task 4: Build Verification (Phase 1)

**Dependencies:** Tasks 1, 2, 3 must all complete
**Files:** None (read-only verification)

- [ ] **Step 1: Build all containers**

```bash
docker compose build
```

Expected: All 3 services build successfully.

- [ ] **Step 2: Start the stack**

```bash
docker compose up -d
```

- [ ] **Step 3: Verify backend health**

```bash
curl -f http://localhost:5000/api/health
```

Expected: `{"status":"healthy","timestamp":"..."}`

- [ ] **Step 4: Verify agent health**

```bash
docker compose exec agent curl -f http://localhost:3001/health
```

Expected: `{"status":"healthy","agentPid":...}`

- [ ] **Step 5: Verify database migration**

```bash
docker compose exec api ls -la /data/knowledge-engine.db
```

Expected: SQLite database file exists.

- [ ] **Step 6: Test book creation via API**

```bash
curl -X POST http://localhost:5000/api/books \
  -H "Content-Type: application/json" \
  -d '{"title":"Test Book","author":"Test Author","year":2024}'
```

Expected: 201 response with book slug.

- [ ] **Step 7: Test book listing**

```bash
curl http://localhost:5000/api/books
```

Expected: JSON array containing the created book.

- [ ] **Step 8: Stop containers**

```bash
docker compose down
```

---

## Phase 2: Conversion Pipeline

### Task 5: MarkItDown CLI Integration

**Dependencies:** Task 4 (Phase 1 complete)
**Files:**
- Create: `backend/Dockerfile` (modify — add Python + markitdown)
- Create: `backend/src/KnowledgeEngine.Api/Services/IConversionService.cs`
- Create: `backend/src/KnowledgeEngine.Api/Services/ConversionService.cs`
- Create: `backend/src/KnowledgeEngine.Api/Endpoints/ConversionEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Program.cs` (register ConversionService)

- [ ] **Step 1: Update `backend/Dockerfile` to include Python and markitdown**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore KnowledgeEngine.sln
RUN dotnet publish KnowledgeEngine.sln -c Release -o /app

FROM python:3.13-slim AS python-base
RUN pip install --no-cache-dir markitdown

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
COPY --from=python-base /usr/local/bin/markitdown /usr/local/bin/markitdown
COPY --from=python-base /usr/local/lib/python3.13/site-packages /usr/local/lib/python3.13/site-packages
COPY --from=python-base /usr/local/lib/libpython3.13.so* /usr/local/lib/
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "KnowledgeEngine.Api.dll"]
```

- [ ] **Step 2: Create `Services/IConversionService.cs`**

```csharp
namespace KnowledgeEngine.Api.Services;

public interface IConversionService
{
    Task<string> ConvertToMarkdownAsync(string inputPath, string outputPath, CancellationToken ct = default);
}
```

- [ ] **Step 3: Create `Services/ConversionService.cs`**

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

        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

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

- [ ] **Step 4: Create `Endpoints/ConversionEndpoints.cs`**

```csharp
using Hangfire;
using KnowledgeEngine.Api.Data;
using KnowledgeEngine.Api.Models;
using KnowledgeEngine.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class ConversionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/convert");

        // Trigger conversion (enqueues Hangfire job)
        group.MapPost("/", async (
            string slug,
            AppDbContext db,
            IConfiguration config,
            IBackgroundJobClient jobClient) =>
        {
            var book = await db.Books.FirstOrDefaultAsync(b => b.Slug == slug);
            if (book is null) return Results.NotFound(new { error = "Book not found" });

            if (string.IsNullOrEmpty(book.SourceFile))
                return Results.BadRequest(new { error = "No source file set for this book" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookDir = Path.Combine(libraryPath, slug);
            var inputPath = Path.Combine(bookDir, book.SourceFile);
            var outputPath = Path.Combine(bookDir, "book.md");

            book.Status = "converting";
            book.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();

            var jobId = jobClient.Enqueue<IConversionService>(x =>
                x.ConvertToMarkdownAsync(inputPath, outputPath));

            // Update book status when job completes via continuation
            BackgroundJob.ContinueWith(jobId, () =>
                UpdateBookAfterConversion(book.Id, slug, outputPath, db));

            return Results.Accepted(new { jobId, status = "queued" });
        });
    }

    // Hangfire continuation job — runs after conversion completes
    public static async Task UpdateBookAfterConversion(
        int bookId, string slug, string outputPath, AppDbContext db)
    {
        // Re-create scope since Hangfire runs in background
        // (The db context will be resolved via Hangfire's DI)
        var book = await db.Books.FindAsync(bookId);
        if (book is null) return;

        if (File.Exists(outputPath))
        {
            book.Status = "ready";
            book.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            book.Status = "error";
            book.ErrorMessage = "Output file was not created";
            book.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 5: Register ConversionService in `Program.cs`**

Add this line before `var app = builder.Build();` in `Program.cs`:

```csharp
builder.Services.AddSingleton<IConversionService, ConversionService>();
```

Also add the conversion endpoints registration after the library endpoints:

```csharp
ConversionEndpoints.Map(app);
```

- [ ] **Step 6: Build and verify**

```bash
cd backend && dotnet build KnowledgeEngine.sln
```

Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add backend/
git commit -m "feat(backend): MarkItDown CLI conversion pipeline with Hangfire jobs"
```

---

### Task 6: Editor Content Endpoints

**Dependencies:** Task 5
**Files:**
- Create: `backend/src/KnowledgeEngine.Api/Endpoints/EditorEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Program.cs` (register endpoints)

- [ ] **Step 1: Create `Endpoints/EditorEndpoints.cs`**

```csharp
using System.Text.Json;
using KnowledgeEngine.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace KnowledgeEngine.Api.Endpoints;

public static class EditorEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/content");

        // Get book markdown content
        group.MapGet("/", async (string slug, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookMd = Path.Combine(libraryPath, slug, "book.md");

            if (!File.Exists(bookMd))
                return Results.NotFound(new { error = "Book content not found. Has it been converted?" });

            var content = await File.ReadAllTextAsync(bookMd);
            return Results.Ok(new { slug, content });
        });

        // Save book markdown content
        group.MapPut("/", async (string slug, UpdateContentRequest req, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var bookMd = Path.Combine(libraryPath, slug, "book.md");

            if (!File.Exists(bookMd))
                return Results.NotFound(new { error = "Book content not found" });

            await File.WriteAllTextAsync(bookMd, req.Content);
            return Results.Ok(new { slug, saved = true });
        });

        // Get book metadata JSON
        group.MapGet("/metadata", async (string slug, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var metaPath = Path.Combine(libraryPath, slug, "metadata.json");

            if (!File.Exists(metaPath))
                return Results.NotFound(new { error = "Metadata not found" });

            var json = await File.ReadAllTextAsync(metaPath);
            var metadata = JsonDocument.Parse(json);
            return Results.Ok(metadata.RootElement);
        });
    }
}

public record UpdateContentRequest(string Content);
```

- [ ] **Step 2: Register editor endpoints in `Program.cs`**

Add after existing endpoint registrations:

```csharp
EditorEndpoints.Map(app);
```

- [ ] **Step 3: Build**

```bash
cd backend && dotnet build KnowledgeEngine.sln
```

- [ ] **Step 4: Commit**

```bash
git add backend/
git commit -m "feat(backend): editor content endpoints (GET/PUT book.md, GET metadata)"
```

---

### Task 7: Pi Agent Service and Chat Endpoints

**Dependencies:** Tasks 5, 6
**Files:**
- Create: `backend/src/KnowledgeEngine.Api/Services/IAgentService.cs`
- Create: `backend/src/KnowledgeEngine.Api/Services/AgentService.cs`
- Create: `backend/src/KnowledgeEngine.Api/Services/ILoreService.cs`
- Create: `backend/src/KnowledgeEngine.Api/Services/LoreService.cs`
- Create: `backend/src/KnowledgeEngine.Api/Endpoints/ChatEndpoints.cs`
- Create: `backend/src/KnowledgeEngine.Api/Endpoints/LoreEndpoints.cs`
- Modify: `backend/src/KnowledgeEngine.Api/Program.cs` (register services and endpoints)

- [ ] **Step 1: Create `Services/IAgentService.cs`**

```csharp
namespace KnowledgeEngine.Api.Services;

public interface IAgentService
{
    /// <summary>
    /// Send a non-streaming prompt to the Pi Agent and return the full response.
    /// </summary>
    Task<string> SendPromptAsync(string message, CancellationToken ct = default);

    /// <summary>
    /// Stream a prompt response from the Pi Agent.
    /// Yields JSON-RPC event strings as they arrive.
    /// </summary>
    IAsyncEnumerable<string> StreamPromptAsync(string message, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create `Services/AgentService.cs`**

```csharp
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KnowledgeEngine.Api.Services;

public class AgentService : IAgentService
{
    private readonly HttpClient _http;
    private readonly ILogger<AgentService> _logger;
    private readonly string _agentBaseUrl;

    public AgentService(HttpClient http, IConfiguration config, ILogger<AgentService> logger)
    {
        _http = http;
        _logger = logger;
        var host = config.GetValue<string>("Agent:Host") ?? "agent";
        var port = config.GetValue<int>("Agent:Port");
        _agentBaseUrl = $"http://{host}:{port}";
    }

    public async Task<string> SendPromptAsync(string message, CancellationToken ct = default)
    {
        var response = await _http.PostAsync($"{_agentBaseUrl}/api/prompt",
            new StringContent(JsonSerializer.Serialize(new { message }), Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        return result.GetProperty("data").ToString();
    }

    public async IAsyncEnumerable<string> StreamPromptAsync(string message, [EnumeratorCancellation] CancellationToken ct = default)
    {
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

- [ ] **Step 3: Create `Services/ILoreService.cs`**

```csharp
namespace KnowledgeEngine.Api.Services;

public interface ILoreService
{
    Task TriggerLoreGenerationAsync(string slug, CancellationToken ct = default);
}
```

- [ ] **Step 4: Create `Services/LoreService.cs`**

```csharp
namespace KnowledgeEngine.Api.Services;

public class LoreService : ILoreService
{
    private readonly IAgentService _agent;
    private readonly IConfiguration _config;
    private readonly ILogger<LoreService> _logger;

    public LoreService(IAgentService agent, IConfiguration config, ILogger<LoreService> logger)
    {
        _agent = agent;
        _config = config;
        _logger = logger;
    }

    public async Task TriggerLoreGenerationAsync(string slug, CancellationToken ct = default)
    {
        var libraryPath = _config.GetValue<string>("Library:Path") ?? "/library";
        var bookMd = Path.Combine(libraryPath, slug, "book.md");

        if (!File.Exists(bookMd))
            throw new FileNotFoundException($"Book content not found: {bookMd}");

        var prompt = $"Read the book at {bookMd} and extract the lore. Generate a character list in {Path.Combine(libraryPath, slug, "wiki", "characters.md")}, locations in {Path.Combine(libraryPath, slug, "wiki", "locations.md")}, themes in {Path.Combine(libraryPath, slug, "wiki", "themes.md")}, and a plot summary in {Path.Combine(libraryPath, slug, "wiki", "summary.md")}.";

        _logger.LogInformation("Triggering lore generation for book: {Slug}", slug);

        try
        {
            await _agent.SendPromptAsync(prompt, ct);
            _logger.LogInformation("Lore generation complete for book: {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lore generation failed for book: {Slug}", slug);
            throw;
        }
    }
}
```

- [ ] **Step 5: Create `Endpoints/ChatEndpoints.cs`**

```csharp
using KnowledgeEngine.Api.Services;

namespace KnowledgeEngine.Api.Endpoints;

public static class ChatEndpoints
{
    public static void Map(WebApplication app)
    {
        // SSE streaming chat endpoint
        app.MapPost("/api/chat", async (
            ChatRequest req,
            IAgentService agentService,
            IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";

            // Contextualize the prompt with the book's content
            var bookMd = Path.Combine(libraryPath, req.BookSlug, "book.md");
            var wikiDir = Path.Combine(libraryPath, req.BookSlug, "wiki");

            var contextParts = new List<string>();

            if (File.Exists(bookMd))
            {
                var content = await File.ReadAllTextAsync(bookMd);
                // Truncate to avoid exceeding context window
                if (content.Length > 50_000)
                    content = content[..50_000] + "\n\n[... truncated ...]";
                contextParts.Add($"# Book Content\n\n{content}");
            }

            var wikiFiles = Directory.Exists(wikiDir)
                ? Directory.GetFiles(wikiDir, "*.md")
                : Array.Empty<string>();

            foreach (var wikiFile in wikiFiles)
            {
                var wikiContent = await File.ReadAllTextAsync(wikiFile);
                if (wikiContent.Length > 10_000)
                    wikiContent = wikiContent[..10_000] + "\n\n[... truncated ...]";
                contextParts.Add($"# {Path.GetFileName(wikiFile)}\n\n{wikiContent}");
            }

            var context = string.Join("\n\n---\n\n", contextParts);
            var fullPrompt = $"You are answering questions about the book. Here is the book's content and wiki:\n\n{context}\n\n---\n\nUser question: {req.Message}";

            return Results.Stream(async (stream, ct) =>
            {
                await foreach (var evt in agentService.StreamPromptAsync(fullPrompt, ct))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes($"data: {evt}\n\n");
                    await stream.WriteAsync(bytes, ct);
                    await stream.FlushAsync(ct);
                }
            }, "text/event-stream");
        });
    }
}
```

- [ ] **Step 6: Create `Endpoints/LoreEndpoints.cs`**

```csharp
using Hangfire;
using KnowledgeEngine.Api.Services;

namespace KnowledgeEngine.Api.Endpoints;

public static class LoreEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/books/{slug}/lore");

        // Trigger lore generation (background job)
        group.MapPost("/", (string slug, IBackgroundJobClient jobClient) =>
        {
            var jobId = jobClient.Enqueue<ILoreService>(x => x.TriggerLoreGenerationAsync(slug));
            return Results.Accepted(new { jobId, status = "queued" });
        });

        // Get lore files listing
        group.MapGet("/", (string slug, IConfiguration config) =>
        {
            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var wikiDir = Path.Combine(libraryPath, slug, "wiki");

            if (!Directory.Exists(wikiDir))
                return Results.Ok(new { files = Array.Empty<string>() });

            var files = Directory.GetFiles(wikiDir, "*.md")
                .Select(f => Path.GetFileName(f))
                .ToArray();

            return Results.Ok(new { files });
        });

        // Get specific lore file content
        group.MapGet("/{file}", async (string slug, string file, IConfiguration config) =>
        {
            // Sanitize file name to prevent path traversal
            if (file.Contains("..") || file.Contains('/') || file.Contains('\\'))
                return Results.BadRequest(new { error = "Invalid file name" });

            var libraryPath = config.GetValue<string>("Library:Path") ?? "/library";
            var filePath = Path.Combine(libraryPath, slug, "wiki", file);

            if (!File.Exists(filePath))
                return Results.NotFound(new { error = "Lore file not found" });

            var content = await File.ReadAllTextAsync(filePath);
            return Results.Ok(new { file, content });
        });
    }
}
```

- [ ] **Step 7: Register all services and endpoints in `Program.cs`**

Add these lines before `var app = builder.Build();`:

```csharp
// Agent service
builder.Services.AddHttpClient<IAgentService, AgentService>();
builder.Services.AddSingleton<ILoreService, LoreService>();
```

Add these lines after existing endpoint registrations:

```csharp
ChatEndpoints.Map(app);
LoreEndpoints.Map(app);
```

- [ ] **Step 8: Build**

```bash
cd backend && dotnet build KnowledgeEngine.sln
```

- [ ] **Step 9: Commit**

```bash
git add backend/
git commit -m "feat(backend): Pi Agent integration with chat SSE streaming and lore generation endpoints"
```

---

### Task 8: Conversion + Agent Integration Test (Phase 2)

**Dependencies:** Tasks 5, 6, 7
**Files:** None (read-only verification)

- [ ] **Step 1: Rebuild containers**

```bash
docker compose build
```

- [ ] **Step 2: Start stack**

```bash
docker compose up -d
```

- [ ] **Step 3: Wait for healthy state**

```bash
# Wait for services to be ready
sleep 10
curl -f http://localhost:5000/api/health
```

- [ ] **Step 4: Test end-to-end flow**

Create a book, copy a test file, trigger conversion, check content:

```bash
# Create book entry
curl -X POST http://localhost:5000/api/books \
  -H "Content-Type: application/json" \
  -d '{"title":"Test Book","author":"Author"}'

# Copy a sample file into the library volume
docker compose exec api sh -c 'echo "# Test Book\n\nThis is a test paragraph.\n\n## Chapter 1\n\nOnce upon a time..." > /library/test-book/book.md'

# Read content
curl http://localhost:5000/api/books/test-book/content

# Update content (PUT)
curl -X PUT http://localhost:5000/api/books/test-book/content \
  -H "Content-Type: application/json" \
  -d '{"content":"# Updated Test Book\n\nNew content here."}'

# Verify update
curl http://localhost:5000/api/books/test-book/content
```

Expected: All endpoints return 200, content is updated.

- [ ] **Step 5: Test lore listing**

```bash
curl http://localhost:5000/api/books/test-book/lore
```

Expected: `{"files":[]}` (no wiki files generated yet).

- [ ] **Step 6: Stop containers**

```bash
docker compose down
```

---

## Phase 3: Frontend (SvelteKit + Milkdown)

### Task 9: SvelteKit Project Setup

**Dependencies:** Task 8 (Phase 2 complete)
**Files:**
- Create: `frontend/` (entire SvelteKit project)

- [ ] **Step 1: Create SvelteKit project**

```bash
cd /home/kahdeg/workspace/continue_story_2
# Remove placeholder Dockerfile so npx create doesn't conflict
rm -f frontend/Dockerfile
npx sv create frontend --template minimal --types ts --no-add-ons --no-install
cd frontend
npm install
```

- [ ] **Step 2: Add Milkdown dependencies**

```bash
cd frontend
npm install @milkdown/kit @milkdown/theme-nord @milkdown-preset/commonmark @milkdown-preset/gfm @milkdown-plugin/listener @milkdown/ctx
```

- [ ] **Step 3: Restore the frontend Dockerfile**

```dockerfile
FROM node:22-alpine AS build
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm install
COPY . .
RUN npm run build

FROM node:22-alpine AS dev
WORKDIR /app
COPY package.json package-lock.json* ./
RUN npm install
COPY . .
EXPOSE 5173
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0"]
```

- [ ] **Step 4: Configure Vite for Docker (proxy API requests)**

Create `frontend/vite.config.ts`:

```typescript
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [sveltekit()],
  server: {
    host: '0.0.0.0',
    proxy: {
      '/api': {
        target: process.env.API_PROXY_TARGET || 'http://api:5000',
        changeOrigin: true,
      },
    },
  },
});
```

- [ ] **Step 5: Verify SvelteKit starts**

```bash
cd frontend && npm run dev &
sleep 5
curl -f http://localhost:5173
kill %1
```

Expected: 200 response with HTML.

- [ ] **Step 6: Commit**

```bash
git add frontend/
git commit -m "feat(frontend): SvelteKit project with Milkdown dependencies and API proxy"
```

---

### Task 10: API Client and Shared Types

**Dependencies:** Task 9
**Files:**
- Create: `frontend/src/lib/api.ts`
- Create: `frontend/src/lib/types.ts`

- [ ] **Step 1: Create `frontend/src/lib/types.ts`**

```typescript
export interface BookSummary {
  id: number;
  slug: string;
  title: string;
  author: string | null;
  year: number | null;
  status: string;
  updatedAt: string;
}

export interface BookDetail {
  id: number;
  slug: string;
  title: string;
  author: string | null;
  year: number | null;
  sourceFile: string | null;
  status: string;
  errorMessage: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface BookContent {
  slug: string;
  content: string;
}

export interface CreateBookRequest {
  title: string;
  author?: string;
  year?: number;
  sourceFile?: string;
}

export interface ChatRequest {
  bookSlug: string;
  message: string;
}

export interface LoreFiles {
  files: string[];
}

export interface LoreContent {
  file: string;
  content: string;
}
```

- [ ] **Step 2: Create `frontend/src/lib/api.ts`**

```typescript
import type { BookSummary, BookDetail, BookContent, CreateBookRequest, LoreFiles, LoreContent } from './types';

const BASE = '/api';

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  if (!res.ok) {
    const error = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(error.error || `HTTP ${res.status}`);
  }
  return res.json();
}

export const api = {
  // Health
  health: () => request<{ status: string } & Record<string, unknown>>('/health'),

  // Books
  listBooks: () => request<BookSummary[]>('/books'),
  getBook: (slug: string) => request<BookDetail>(`/books/${slug}`),
  createBook: (data: CreateBookRequest) =>
    request<BookDetail>('/books', {
      method: 'POST',
      body: JSON.stringify(data),
    }),
  deleteBook: (slug: string) =>
    fetch(`${BASE}/books/${slug}`, { method: 'DELETE' }).then((r) => {
      if (!r.ok) throw new Error('Delete failed');
    }),

  // Editor
  getBookContent: (slug: string) => request<BookContent>(`/books/${slug}/content`),
  saveBookContent: (slug: string, content: string) =>
    request<{ slug: string; saved: boolean }>(`/books/${slug}/content`, {
      method: 'PUT',
      body: JSON.stringify({ content }),
    }),

  // Conversion
  triggerConversion: (slug: string) =>
    request<{ jobId: string; status: string }>(`/books/${slug}/convert`, {
      method: 'POST',
    }),

  // Lore
  getLoreFiles: (slug: string) => request<LoreFiles>(`/books/${slug}/lore`),
  getLoreContent: (slug: string, file: string) =>
    request<LoreContent>(`/books/${slug}/lore/${encodeURIComponent(file)}`),
  triggerLoreGeneration: (slug: string) =>
    request<{ jobId: string; status: string }>(`/books/${slug}/lore`, {
      method: 'POST',
    }),

  // Chat (SSE)
  chat(bookSlug: string, message: string, onChunk: (data: string) => void, onDone: () => void): AbortController {
    const controller = new AbortController();
    fetch(`${BASE}/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bookSlug, message }),
      signal: controller.signal,
    })
      .then(async (res) => {
        const reader = res.body?.getReader();
        if (!reader) return;
        const decoder = new TextDecoder();
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          const text = decoder.decode(value);
          for (const line of text.split('\n')) {
            if (line.startsWith('data: ')) {
              try {
                const evt = JSON.parse(line.slice(6));
                if (evt.type === 'agent_end') {
                  onDone();
                } else if (evt.type === 'message_update') {
                  const delta = evt.assistantMessageEvent;
                  if (delta?.type === 'text_delta') {
                    onChunk(delta.delta);
                  }
                }
              } catch {
                // Ignore parse errors
              }
            }
          }
        }
        onDone();
      })
      .catch((err) => {
        if (err.name !== 'AbortError') console.error('Chat error:', err);
      });
    return controller;
  },
};
```

- [ ] **Step 3: Commit**

```bash
git add frontend/src/lib/
git commit -m "feat(frontend): API client and shared TypeScript types"
```

---

### Task 11: App Layout and Book List Component

**Dependencies:** Task 10
**Files:**
- Create: `frontend/src/app.html`
- Create: `frontend/src/app.d.ts`
- Create: `frontend/src/routes/+layout.svelte`
- Create: `frontend/src/routes/+page.svelte`
- Create: `frontend/src/lib/components/BookList.svelte`

- [ ] **Step 1: Create `frontend/src/app.html`**

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8" />
    <link rel="icon" href="%sveltekit.assets%/favicon.png" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    %sveltekit.head%
  </head>
  <body data-sveltekit-preload-data="hover">
    <div style="display: contents">%sveltekit.body%</div>
  </body>
</html>
```

- [ ] **Step 2: Create `frontend/src/app.d.ts`**

```typescript
// See https://svelte.dev/docs/kit/types#app.d.ts
// for information about these interfaces
declare global {
  namespace App {
    // interface Error {}
    // interface Locals {}
    // interface PageData {}
    // interface PageState {}
    // interface Platform {}
  }
}

export {};
```

- [ ] **Step 3: Create `frontend/src/routes/+layout.svelte`**

```svelte
<script lang="ts">
  import '../app.css';

  let { children } = $props();
</script>

<div class="app-layout">
  {children}
</div>

<style>
  :global(*) {
    box-sizing: border-box;
    margin: 0;
    padding: 0;
  }

  :global(body) {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
    background: #0d1117;
    color: #c9d1d9;
  }

  .app-layout {
    display: flex;
    height: 100vh;
    width: 100vw;
  }
</style>
```

- [ ] **Step 4: Create `frontend/src/app.css`**

```css
:root {
  --bg-primary: #0d1117;
  --bg-secondary: #161b22;
  --bg-tertiary: #21262d;
  --border: #30363d;
  --text-primary: #c9d1d9;
  --text-secondary: #8b949e;
  --accent: #58a6ff;
  --accent-hover: #79c0ff;
  --success: #3fb950;
  --warning: #d29922;
  --error: #f85149;
}
```

- [ ] **Step 5: Create `frontend/src/routes/+page.svelte` (home/library view)**

```svelte
<script lang="ts">
  import { onMount } from 'svelte';
  import BookList from '$lib/components/BookList.svelte';
  import { api } from '$lib/api';
  import type { BookSummary } from '$lib/types';

  let books: BookSummary[] = $state([]);
  let loading = $state(true);
  let showCreateForm = $state(false);
  let newTitle = $state('');
  let newAuthor = $state('');

  async function loadBooks() {
    try {
      books = await api.listBooks();
    } catch (err) {
      console.error('Failed to load books:', err);
    } finally {
      loading = false;
    }
  }

  async function createBook() {
    if (!newTitle.trim()) return;
    try {
      await api.createBook({
        title: newTitle.trim(),
        author: newAuthor.trim() || undefined,
      });
      newTitle = '';
      newAuthor = '';
      showCreateForm = false;
      await loadBooks();
    } catch (err) {
      console.error('Failed to create book:', err);
    }
  }

  onMount(loadBooks);
</script>

<div class="library-page">
  <aside class="sidebar">
    <div class="sidebar-header">
      <h1>📚 Library</h1>
      <button class="btn btn-primary" onclick={() => showCreateForm = !showCreateForm}>
        + New Book
      </button>
    </div>

    {#if showCreateForm}
      <form class="create-form" onsubmit={(e) => { e.preventDefault(); createBook(); }}>
        <input placeholder="Title" bind:value={newTitle} required />
        <input placeholder="Author" bind:value={newAuthor} />
        <div class="form-actions">
          <button type="submit" class="btn btn-primary">Create</button>
          <button type="button" class="btn" onclick={() => showCreateForm = false}>Cancel</button>
        </div>
      </form>
    {/if}

    {#if loading}
      <p class="loading">Loading...</p>
    {:else}
      <BookList {books} />
    {/if}
  </aside>

  <main class="main-content">
    <div class="empty-state">
      <h2>Welcome to Knowledge Engine</h2>
      <p>Select a book from the library to start reading and editing.</p>
    </div>
  </main>
</div>

<style>
  .library-page {
    display: flex;
    width: 100%;
    height: 100vh;
  }

  .sidebar {
    width: 280px;
    min-width: 280px;
    background: var(--bg-secondary);
    border-right: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  .sidebar-header {
    padding: 16px;
    border-bottom: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    gap: 12px;
  }

  .sidebar-header h1 {
    font-size: 18px;
    font-weight: 600;
    color: var(--text-primary);
  }

  .create-form {
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  .create-form input {
    padding: 8px 12px;
    background: var(--bg-tertiary);
    border: 1px solid var(--border);
    border-radius: 6px;
    color: var(--text-primary);
    font-size: 14px;
  }

  .form-actions {
    display: flex;
    gap: 8px;
  }

  .btn {
    padding: 6px 12px;
    border: 1px solid var(--border);
    border-radius: 6px;
    background: var(--bg-tertiary);
    color: var(--text-primary);
    cursor: pointer;
    font-size: 13px;
  }

  .btn-primary {
    background: #238636;
    border-color: #238636;
    color: white;
  }

  .btn-primary:hover {
    background: #2ea043;
  }

  .loading {
    padding: 16px;
    color: var(--text-secondary);
  }

  .main-content {
    flex: 1;
    display: flex;
    align-items: center;
    justify-content: center;
  }

  .empty-state {
    text-align: center;
    color: var(--text-secondary);
  }

  .empty-state h2 {
    font-size: 24px;
    margin-bottom: 8px;
  }
</style>
```

- [ ] **Step 6: Create `frontend/src/lib/components/BookList.svelte`**

```svelte
<script lang="ts">
  import type { BookSummary } from '$lib/types';

  let { books }: { books: BookSummary[] } = $props();

  function statusIcon(status: string): string {
    switch (status) {
      case 'ready': return '✅';
      case 'converting': return '⏳';
      case 'error': return '❌';
      default: return '📄';
    }
  }
</script>

<div class="book-list">
  {#if books.length === 0}
    <p class="empty">No books yet. Create one to get started.</p>
  {:else}
    {#each books as book (book.slug)}
      <a href="/books/{book.slug}" class="book-item">
        <span class="status-icon">{statusIcon(book.status)}</span>
        <div class="book-info">
          <span class="book-title">{book.title}</span>
          {#if book.author}
            <span class="book-author">{book.author}</span>
          {/if}
        </div>
      </a>
    {/each}
  {/if}
</div>

<style>
  .book-list {
    flex: 1;
    overflow-y: auto;
    padding: 8px;
  }

  .empty {
    padding: 16px;
    color: var(--text-secondary);
    font-size: 13px;
  }

  .book-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 10px 12px;
    border-radius: 6px;
    text-decoration: none;
    color: var(--text-primary);
    transition: background 0.15s;
  }

  .book-item:hover {
    background: var(--bg-tertiary);
  }

  .status-icon {
    font-size: 16px;
    flex-shrink: 0;
  }

  .book-info {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }

  .book-title {
    font-size: 14px;
    font-weight: 500;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .book-author {
    font-size: 12px;
    color: var(--text-secondary);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }
</style>
```

- [ ] **Step 7: Commit**

```bash
git add frontend/src/
git commit -m "feat(frontend): app layout, book list, and library page"
```

---

### Task 12: Milkdown Editor Component

**Dependencies:** Task 11
**Files:**
- Create: `frontend/src/lib/components/BookEditor.svelte`
- Create: `frontend/src/routes/books/[slug]/+page.svelte`

- [ ] **Step 1: Create `frontend/src/lib/components/BookEditor.svelte`**

This component wraps Milkdown and supports the readonly/read-write toggle:

```svelte
<script lang="ts">
  import { onMount } from 'svelte';
  import { Editor, rootCtx, defaultValueCtx } from '@milkdown/kit/core';
  import { commonmark } from '@milkdown/preset/commonmark';
  import { gfm } from '@milkdown/preset/gfm';
  import { nord } from '@milkdown/theme-nord';
  import { listener, listenerCtx } from '@milkdown/plugin-listener';

  let { content = '', readonly = $bindable(false), onContentChange }: {
    content: string;
    readonly: boolean;
    onContentChange?: (markdown: string) => void;
  } = $props();

  let editorEl: HTMLDivElement;
  let editor: Editor | null = $state(null);

  onMount(async () => {
    editor = await Editor.make()
      .config((ctx) => {
        ctx.set(rootCtx, editorEl);
        ctx.set(defaultValueCtx, content);
        ctx.set(nord);
        ctx.set(listenerCtx, {});
      })
      .config(nord)
      .use(commonmark)
      .use(gfm)
      .use(listener)
      .create();

    // Listen for content changes
    const listenerManager = editor.ctx.get(listenerCtx);
    listenerManager.markdownUpdated((ctx, doc, prevDoc) => {
      if (prevDoc) {
        const markdown = editor!.action((ctx) => {
          const editorView = ctx.get(rootCtx);
          // Get serialized markdown from the editor
          return editorView.dom.textContent;
        });
        onContentChange?.(markdown);
      }
    });
  });

  $effect(() => {
    if (!editor) return;
    // Toggle readonly
    const editorView = editor.ctx.get(rootCtx);
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

- [ ] **Step 2: Create `frontend/src/routes/books/[slug]/+page.svelte`**

```svelte
<script lang="ts">
  import { page } from '$app/stores';
  import { onMount } from 'svelte';
  import BookEditor from '$lib/components/BookEditor.svelte';
  import ChatPanel from '$lib/components/ChatPanel.svelte';
  import LorePanel from '$lib/components/LorePanel.svelte';
  import { api } from '$lib/api';
  import type { BookDetail } from '$lib/types';

  let slug = $derived($page.params.slug);
  let book: BookDetail | null = $state(null);
  let content = $state('');
  let loading = $state(true);
  let error = $state('');
  let isEditing = $state(false);
  let saving = $state(false);
  let showChat = $state(false);
  let showLore = $state(false);

  async function loadBook() {
    loading = true;
    error = '';
    try {
      book = await api.getBook(slug);
      if (book.status === 'ready') {
        const result = await api.getBookContent(slug);
        content = result.content;
      }
    } catch (err: any) {
      error = err.message;
    } finally {
      loading = false;
    }
  }

  async function saveContent(newContent: string) {
    if (saving || !isEditing) return;
    saving = true;
    try {
      await api.saveBookContent(slug, newContent);
      content = newContent;
    } catch (err) {
      console.error('Save failed:', err);
    } finally {
      saving = false;
    }
  }

  async function triggerConversion() {
    try {
      await api.triggerConversion(slug);
      // Poll for completion
      const interval = setInterval(async () => {
        await loadBook();
        if (book?.status === 'ready' || book?.status === 'error') {
          clearInterval(interval);
          if (book.status === 'ready') {
            const result = await api.getBookContent(slug);
            content = result.content;
          }
        }
      }, 3000);
    } catch (err) {
      console.error('Conversion failed:', err);
    }
  }

  onMount(loadBook);
</script>

{#if loading}
  <div class="loading-screen">Loading book...</div>
{:else if error}
  <div class="error-screen">
    <p>{error}</p>
    <a href="/" class="back-link">← Back to Library</a>
  </div>
{:else if book}
  <div class="book-view">
    <!-- Toolbar -->
    <div class="toolbar">
      <a href="/" class="back-link">← Library</a>
      <h2 class="book-title">{book.title}</h2>
      <div class="toolbar-actions">
        {#if book.status === 'pending'}
          <button class="btn btn-primary" onclick={triggerConversion}>
            Convert
          </button>
        {:else if book.status === 'converting'}
          <span class="status-converting">⏳ Converting...</span>
        {:else if book.status === 'ready'}
          <button class="btn" onclick={() => isEditing = !isEditing}>
            {isEditing ? '🔒 Lock' : '✏️ Edit'}
          </button>
          {#if saving}
            <span class="status-saving">Saving...</span>
          {/if}
        {:else if book.status === 'error'}
          <span class="status-error">❌ Conversion failed</span>
        {/if}
        <button class="btn" onclick={() => showLore = !showLore}>
          📖 Wiki
        </button>
        <button class="btn" onclick={() => showChat = !showChat}>
          💬 Chat
        </button>
      </div>
    </div>

    <!-- Main area -->
    <div class="main-area">
      <div class="editor-pane">
        {#if book.status === 'ready'}
          <BookEditor
            bind:content
            readonly={!isEditing}
            onContentChange={(md) => { if (isEditing) saveContent(md); }}
          />
        {:else}
          <div class="empty-editor">
            {#if book.status === 'pending'}
              <p>Book not yet converted. Click "Convert" to process it.</p>
            {:else if book.status === 'converting'}
              <p>Conversion in progress...</p>
            {:else}
              <p>Conversion failed: {book.errorMessage}</p>
            {/if}
          </div>
        {/if}
      </div>

      <!-- Side panels -->
      {#if showLore}
        <div class="side-panel">
          <LorePanel {slug} />
        </div>
      {/if}

      {#if showChat}
        <div class="side-panel">
          <ChatPanel {slug} />
        </div>
      {/if}
    </div>
  </div>
{/if}

<style>
  .loading-screen, .error-screen {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100vh;
    gap: 16px;
    color: var(--text-secondary);
  }

  .book-view {
    display: flex;
    flex-direction: column;
    width: 100%;
    height: 100vh;
  }

  .toolbar {
    display: flex;
    align-items: center;
    gap: 16px;
    padding: 12px 24px;
    background: var(--bg-secondary);
    border-bottom: 1px solid var(--border);
  }

  .back-link {
    color: var(--accent);
    text-decoration: none;
    font-size: 14px;
  }

  .book-title {
    font-size: 16px;
    font-weight: 600;
    flex: 1;
  }

  .toolbar-actions {
    display: flex;
    align-items: center;
    gap: 8px;
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

  .btn-primary {
    background: #238636;
    border-color: #238636;
    color: white;
  }

  .status-converting, .status-saving {
    color: var(--warning);
    font-size: 13px;
  }

  .status-error {
    color: var(--error);
    font-size: 13px;
  }

  .main-area {
    flex: 1;
    display: flex;
    overflow: hidden;
  }

  .editor-pane {
    flex: 1;
    overflow-y: auto;
  }

  .empty-editor {
    display: flex;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--text-secondary);
  }

  .side-panel {
    width: 360px;
    min-width: 360px;
    border-left: 1px solid var(--border);
    background: var(--bg-secondary);
    overflow-y: auto;
  }
</style>
```

- [ ] **Step 3: Commit**

```bash
git add frontend/src/
git commit -m "feat(frontend): Milkdown editor component with book page and toolbar"
```

---

### Task 13: Chat and Lore Panel Components

**Dependencies:** Task 12
**Files:**
- Create: `frontend/src/lib/components/ChatPanel.svelte`
- Create: `frontend/src/lib/components/LorePanel.svelte`

- [ ] **Step 1: Create `frontend/src/lib/components/ChatPanel.svelte`**

```svelte
<script lang="ts">
  import { api } from '$lib/api';

  let { slug }: { slug: string } = $props();

  let messages: Array<{ role: 'user' | 'assistant'; text: string }> = $state([]);
  let input = $state('');
  let streaming = $state(false);
  let currentResponse = $state('');
  let chatContainer: HTMLDivElement;

  async function send() {
    const msg = input.trim();
    if (!msg || streaming) return;

    messages = [...messages, { role: 'user', text: msg }];
    input = '';
    streaming = true;
    currentResponse = '';

    const controller = api.chat(
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
      }
    );

    // Scroll to bottom
    $effect(() => {
      if (chatContainer) {
        chatContainer.scrollTop = chatContainer.scrollHeight;
      }
    });
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  }
</script>

<div class="chat-panel">
  <h3 class="panel-title">💬 AI Chat</h3>

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
        <div class="message-text">{currentResponse}<span class="cursor">▌</span></div>
      </div>
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

- [ ] **Step 2: Create `frontend/src/lib/components/LorePanel.svelte`**

```svelte
<script lang="ts">
  import { onMount } from 'svelte';
  import { api } from '$lib/api';
  import type { LoreFiles, LoreContent } from '$lib/types';

  let { slug }: { slug: string } = $props();

  let files: string[] = $state([]);
  let activeFile: string | null = $state(null);
  let content = $state('');
  let loading = $state(false);
  let generating = $state(false);

  async function loadFiles() {
    try {
      const result = await api.getLoreFiles(slug);
      files = result.files;
    } catch {
      files = [];
    }
  }

  async function loadFile(file: string) {
    loading = true;
    activeFile = file;
    try {
      const result = await api.getLoreContent(slug, file);
      content = result.content;
    } catch (err: any) {
      content = `Error loading file: ${err.message}`;
    } finally {
      loading = false;
    }
  }

  async function generate() {
    generating = true;
    try {
      await api.triggerLoreGeneration(slug);
      // Poll for files
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

  onMount(loadFiles);
</script>

<div class="lore-panel">
  <h3 class="panel-title">📖 Wiki</h3>

  <div class="lore-actions">
    <button class="btn btn-primary" onclick={generate} disabled={generating}>
      {generating ? '⏳ Generating...' : '🪄 Generate Lore'}
    </button>
  </div>

  <div class="file-tabs">
    {#each files as file}
      <button
        class="file-tab"
        class:active={activeFile === file}
        onclick={() => loadFile(file)}
      >
        {file}
      </button>
    {/each}
  </div>

  <div class="lore-content">
    {#if activeFile && loading}
      <p class="loading">Loading...</p>
    {:else if activeFile}
      <pre>{content}</pre>
    {:else if files.length === 0}
      <p class="empty-hint">No lore generated yet. Click "Generate Lore" to analyze the book.</p>
    {/if}
  </div>
</div>

<style>
  .lore-panel {
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

  .lore-actions {
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
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

  .btn-primary {
    background: #238636;
    border-color: #238636;
    color: white;
  }

  .btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }

  .file-tabs {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
    padding: 8px 16px;
    border-bottom: 1px solid var(--border);
  }

  .file-tab {
    padding: 4px 10px;
    border: 1px solid var(--border);
    border-radius: 4px;
    background: transparent;
    color: var(--text-secondary);
    cursor: pointer;
    font-size: 12px;
  }

  .file-tab.active {
    background: var(--bg-tertiary);
    color: var(--text-primary);
  }

  .lore-content {
    flex: 1;
    overflow-y: auto;
    padding: 16px;
  }

  .lore-content pre {
    font-size: 13px;
    line-height: 1.6;
    white-space: pre-wrap;
    word-break: break-word;
    color: var(--text-primary);
  }

  .loading, .empty-hint {
    color: var(--text-secondary);
    font-size: 13px;
    text-align: center;
    padding-top: 24px;
  }
</style>
```

- [ ] **Step 3: Commit**

```bash
git add frontend/src/lib/components/
git commit -m "feat(frontend): chat panel with SSE streaming and lore wiki panel"
```

---

## Phase 4: Integration and Polish

### Task 14: Docker Compose Integration (Full Stack)

**Dependencies:** Tasks 8, 13 (Phases 2 and 3 complete)
**Files:**
- Modify: `docker-compose.yml` (refine dev mode target for frontend)

- [ ] **Step 1: Update `docker-compose.yml` to use dev target for frontend**

```yaml
services:
  api:
    build:
      context: ./backend
      dockerfile: Dockerfile
    ports:
      - "5000:5000"
    volumes:
      - library-data:/library
      - sqlite-data:/data
    environment:
      - ASPNETCORE_ENVIRONMENT=Development
      - LIBRARY_PATH=/library
      - SQLITE_PATH=/data/knowledge-engine.db
      - AGENT_HOST=agent
      - AGENT_PORT=3001
    depends_on:
      agent:
        condition: service_started

  frontend:
    build:
      context: ./frontend
      dockerfile: Dockerfile
      target: dev
    ports:
      - "5173:5173"
    environment:
      - API_PROXY_TARGET=http://api:5000
    volumes:
      - ./frontend/src:/app/src
      - ./frontend/static:/app/static
      - ./frontend/svelte.config.js:/app/svelte.config.js
      - ./frontend/vite.config.ts:/app/vite.config.ts
    depends_on:
      api:
        condition: service_started

  agent:
    build:
      context: ./agent
      dockerfile: Dockerfile
    volumes:
      - library-data:/library:ro
      - ./skills:/skills:ro
    environment:
      - PI_CWD=/library
      - PORT=3001
    healthcheck:
      test: ["CMD", "wget", "-q", "--spider", "http://localhost:3001/health"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 15s

volumes:
  library-data:
  sqlite-data:
```

- [ ] **Step 2: Build all containers**

```bash
docker compose build
```

Expected: All 3 services build successfully.

- [ ] **Step 3: Start full stack**

```bash
docker compose up -d
```

- [ ] **Step 4: Verify all services are healthy**

```bash
sleep 15
curl -f http://localhost:5000/api/health
curl -f http://localhost:5173
```

Expected: Both return 200.

- [ ] **Step 5: Test end-to-end book flow via UI**

```bash
# Create a book
curl -X POST http://localhost:5000/api/books \
  -H "Content-Type: application/json" \
  -d '{"title":"The Great Gatsby","author":"F. Scott Fitzgerald","year":1925}'

# Seed some content directly into the volume
docker compose exec api sh -c 'cat > /library/the-great-gatsby/book.md << '\''EOF'\''
# The Great Gatsby

## Chapter 1

In my younger and more vulnerable years my father gave me some advice that I have been turning over in my mind ever since.

"Whenever you feel like criticizing anyone," he told me, "just remember that all the people in this world have not had the advantages that you have had."

He did not say any more, but we have always been unusually communicative in a reserved way, and I understood that he meant a great deal more than that.
EOF'

# Read it back
curl http://localhost:5000/api/books/the-great-gatsby/content
```

Expected: Markdown content returned.

- [ ] **Step 6: Verify frontend renders in browser**

Open `http://localhost:5173` in a browser. Verify:
- Library page shows the created book
- Clicking the book opens the editor view
- Content is displayed

- [ ] **Step 7: Stop containers**

```bash
docker compose down
```

- [ ] **Step 8: Commit**

```bash
git add docker-compose.yml
git commit -m "feat: docker compose integration with dev volumes and health checks"
```

---

### Task 15 (Final): End-to-End Verification

**Dependencies:** Task 14
**Files:** None (read-only verification)

- [ ] **Step 1: Clean start**

```bash
docker compose down -v  # Remove volumes for clean state
docker compose build
docker compose up -d
```

- [ ] **Step 2: Wait for all services healthy**

```bash
sleep 20
curl -f http://localhost:5000/api/health
```

Expected: `{"status":"healthy",...}`

- [ ] **Step 3: Run the full book lifecycle**

```bash
# 1. Create a book
curl -s -X POST http://localhost:5000/api/books \
  -H "Content-Type: application/json" \
  -d '{"title":"Test Book","author":"Test Author"}' | jq .

# 2. Seed markdown content
docker compose exec api sh -c 'mkdir -p /library/test-book && echo "# Test\n\nHello world." > /library/test-book/book.md'

# 3. Read content via API
curl -s http://localhost:5000/api/books/test-book/content | jq .content

# 4. Update content via API
curl -s -X PUT http://localhost:5000/api/books/test-book/content \
  -H "Content-Type: application/json" \
  -d '{"content":"# Updated\n\nNew content."}' | jq .

# 5. Verify update
curl -s http://localhost:5000/api/books/test-book/content | jq .content

# 6. List lore (should be empty)
curl -s http://localhost:5000/api/books/test-book/lore | jq .

# 7. List all books
curl -s http://localhost:5000/api/books | jq .

# 8. Delete the book
curl -s -X DELETE http://localhost:5000/api/books/test-book
echo "Exit code: $?"

# 9. Verify deletion
curl -s http://localhost:5000/api/books | jq .
```

Expected: All operations succeed with correct HTTP status codes.

- [ ] **Step 4: Verify frontend loads**

```bash
curl -f http://localhost:5173
```

Expected: HTML page with SvelteKit app shell.

- [ ] **Step 5: Verify plan success criteria**

Check each criterion:
- [ ] **Book-as-folder:** Books are stored as directories under `/library/{slug}/` with `book.md`
- [ ] **MarkItDown conversion:** `markitdown` CLI is available in the API container
- [ ] **Milkdown editor:** Frontend loads and displays markdown content (verify in browser)
- [ ] **Readonly/read-write toggle:** Editor toolbar has Edit/Lock button
- [ ] **Pi Agent RPC sidecar:** Agent container runs and responds to `/health`
- [ ] **Chat streaming:** `/api/chat` endpoint accepts POST and streams SSE
- [ ] **Lore generation:** `/api/books/{slug}/lore` POST endpoint queues Hangfire job
- [ ] **Hangfire jobs:** Background job processing is configured (SQLite storage)
- [ ] **Docker Compose:** All 3 containers start and communicate
- [ ] **No auth:** No authentication middleware is present

- [ ] **Step 6: Stop and clean up**

```bash
docker compose down -v
```
