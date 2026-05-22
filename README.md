# Story Engine

An AI-powered novel writing platform. Upload manuscripts (EPUB, PDF, DOCX, TXT, and more), split them into chapters with LLM-generated titles, chat with an AI agent to edit and refine prose, manage characters and worldbuilding via a wiki, and maintain consistency across your story.

Built with a Svelte 5 frontend, a .NET 8 API backend, and a Node.js AI agent powered by pi-agent-core.

## Architecture

```
┌──────────────┐     ┌─────────────┐     ┌─────────────┐
│  Frontend    │────▶│  API (.NET) │────▶│  Agent      │
│  Svelte 5    │◀────│  Port 5000  │◀────│  Port 3001  │
│  Port 5173   │     │             │     │  (LLM)      │
└──────────────┘     └──────┬──────┘     └─────────────┘
                            │
                     ┌──────┴──────┐
                     │  SQLite DB  │
                     │  (Hangfire  │
                     │   + App)    │
                     └─────────────┘
```

- **Frontend** — Svelte 5 + Vite. Book reader/editor, chat panel, wiki manager, knowledge base.
- **API** — ASP.NET Core 8. REST endpoints for books, chapters, chat, conversion, wiki/lore. Hangfire for background jobs (conversion, chapter splitting, lore generation).
- **Agent** — Node.js HTTP server wrapping pi-agent-core. Manages AI sessions per book, processes chat prompts, runs file read/write tools for story editing. System prompt includes writing style guidance and AI slop removal rules.
- **SearXNG** — Local metasearch engine for the agent's web research tool.

## Services

| Service | Port | Description |
|---|---|---|
| frontend | 5173 | Svelte 5 UI (Vite dev server) |
| api | 5000 | ASP.NET Core REST API |
| agent | 3001 | Node.js AI agent (pi-agent-core) |
| searxng | 8080 | Private metasearch engine |

## Quick Start

```bash
# Copy environment template
cp .env.example .env

# Edit .env with your API keys (at least one LLM provider)
#   OPENAI_API_KEY=sk-...
#   ANTHROPIC_API_KEY=sk-ant-...
#   DEEPSEEK_API_KEY=sk-...
#   PI_MODEL=openai/gpt-4o

# Start all services
docker-compose up -d

# Open the UI
open http://localhost:5173
```

## Usage

### Creating a book

1. Open the library page at `/`
2. Click "+ Empty Book" for a blank manuscript, or drag-and-drop a file
3. Supported formats: EPUB, PDF, DOCX, DOC, TXT, HTML, PPTX, XLSX, CSV, IPYNB, MD

### Chapter splitting

After uploading, the book goes through automated conversion → chapter splitting → lore generation. You can also trigger splitting manually via the book's chapter list.

### Chat with the AI

The chat panel on each book page lets you:
- Ask questions about the story and characters
- Request edits to specific chapters (the agent writes scratch files, you review diffs)
- Research topics via web search (SearXNG)
- The agent has full file read/write access to your book's chapters and wiki

### Wiki / Lore

Manage characters, locations, and plot summaries per book. The wiki is stored as markdown files in `library/{slug}/wiki/`. The agent reads and updates these as it works.

### Knowledge Base

A shared research library at `library/shared/` available across all stories. Topics are organized into fixed categories:
- `research/` — Real-world topic research
- `worldbuilding/` — Fictional world settings
- `themes/` — Story themes and tropes
- `references/` — Writing references and style guides

## Project Structure

```
├── agent/                  # Node.js AI agent
│   ├── src/
│   │   ├── index.ts        # HTTP server + session management
│   │   └── web-tools.ts    # Web search/fetch tools
│   ├── Dockerfile
│   └── package.json
├── backend/                # .NET 8 API
│   └── src/StoryEngine.Api/
│       ├── Endpoints/      # REST endpoint handlers
│       ├── Services/       # AgentService, ChapterSplitService, LoreJobService, etc.
│       ├── Data/           # EF Core DbContext
│       ├── Models/         # Entity models
│       └── Program.cs
├── frontend/               # Svelte 5 UI
│   └── src/
│       ├── lib/
│       │   ├── components/ # BookList, BookEditor, ChatPanel, ChapterSidebar, etc.
│       │   ├── api.ts      # API client
│       │   └── types.ts    # TypeScript types
│       └── routes/         # SvelteKit routes
├── library/                # Book storage (bind-mounted volume)
│   └── shared/             # Shared knowledge base
├── searxng/                # SearXNG config
├── docker-compose.yml
└── README.md
```

## Agent System Prompt

The agent's system prompt includes:

- **Role** — Creative writing assistant with file tools
- **Writing Style** — Literary, immersive prose with varied sentence structure and sensory detail
- **Remove AI Slop** — Banned words, filler phrases, negation crutches, meta-writing, hedging, corporate-speak, LLM sentence starters
- **Creative Freedom** — No content restrictions, no disclaimers, no moralizing

## Environment Variables

| Variable | Required | Default | Description |
|---|---|---|---|
| `ANTHROPIC_API_KEY` | No | — | Anthropic API key |
| `OPENAI_API_KEY` | No | — | OpenAI API key |
| `OPENAI_BASE_URL` | No | — | Custom OpenAI-compatible base URL |
| `GOOGLE_API_KEY` | No | — | Google AI API key |
| `OPENROUTER_API_KEY` | No | — | OpenRouter API key |
| `DEEPSEEK_API_KEY` | No | — | DeepSeek API key |
| `PI_MODEL` | Yes | `deepseek/deepseek-v4-flash` | Provider/model identifier (e.g. `openai/gpt-4o`) |
| `MAX_SESSIONS` | No | 10 | Max concurrent agent sessions |
| `SEARXNG_URL` | No | `http://searxng:8080` | SearXNG instance URL |

## Development

```bash
# Start with hot-reload
docker-compose up -d

# View logs
docker-compose logs -f agent
docker-compose logs -f api
docker-compose logs -f frontend

# Rebuild a single service
docker-compose build agent
docker-compose up -d agent

# Run tests
docker-compose exec api dotnet test
```

## License

Private project.
