# Chat Tool Call & Thinking Display Plan

**Goal:** Show tool execution progress and thinking in the chat UI so users can see what the agent is doing (reading, writing, searching, etc.) instead of a blank spinner during tool execution.

**Architecture:** The agent already emits `tool_execution_start`, `tool_execution_end`, and `thinking_delta` events through SSE. The backend passes them through. The frontend `api.ts` already handles `thinking_delta` but ignores tool events. The fix is purely frontend — handle the ignored events and render them as inline activity blocks in the chat message stream.

**Scope:**
- Frontend only (no backend/agent changes needed)
- Both ChatPanel.svelte (book chat) and knowledge page chat
- Fix KnowledgeChatEndpoints double-serialization bug while we're at it

**Verification:** `docker compose build && docker compose up -d` + visual check

---

## File Structure

### New files

| File | Responsibility |
|---|---|
| `frontend/src/lib/tool-labels.ts` | Shared abstraction layer — maps raw tool calls to human-readable activity descriptions |

### Modified files

| File | Change |
|---|---|
| `frontend/src/lib/api.ts` | Add `onToolStart`, `onToolEnd` callbacks to `chat()` and `knowledgeChat()` |
| `frontend/src/lib/components/ChatPanel.svelte` | Render tool execution blocks inline during streaming |
| `frontend/src/routes/knowledge/+page.svelte` | Same tool display + fix streaming text + fix KB chat SSE |
| `backend/src/StoryEngine.Api/Endpoints/KnowledgeChatEndpoints.cs` | Fix double-serialization bug |

### Abstraction Layer

The `tool-labels.ts` module translates raw internal tool calls into user-facing activity descriptions. **Zero raw paths, tool names, or file extensions leak through.**

Examples of the translation:

| Raw tool call | User sees |
|---|---|
| `read("chapters/ch-003-the-beginning.md")` | Reading Chapter 3: The Beginning |
| `write("chapters/ch-003.scratch.md")` | Editing Chapter 3 |
| `bash("ls chapters/")` | Listing chapters |
| `bash("grep -n 'pattern' chapters/*.md")` | Searching text for 'pattern' |
| `read("wiki/characters/yuki-tanaka.md")` | Reading character: Yuki Tanaka |
| `write("wiki/locations/old-school.md")` | Updating location: Old School |
| `web_search({ query: "Japanese folklore" })` | Searching the web for 'Japanese folklore' |
| `web_fetch({ url: "https://en.wikipedia.org/wiki/Yokai" })` | Fetching en.wikipedia.org |
| `read("research/japanese-mythology.md")` (KB) | Reading: Japanese Mythology |
| `write("worldbuilding/magic-system.md")` (KB) | Writing: Magic System |

---

### Task 1: Fix KnowledgeChatEndpoints double-serialization

**Dependencies:** None

The KB chat endpoint double-serializes SSE events — `evt` is already a JSON string from `StreamPromptAsync`, but `JsonSerializer.Serialize(evt)` wraps it in quotes.

- [ ] In `backend/src/StoryEngine.Api/Endpoints/KnowledgeChatEndpoints.cs`, find the SSE streaming line:
  ```csharp
  await ctx.Response.WriteAsync($"data: { JsonSerializer.Serialize(evt) }\n\n");
  ```
  Replace with:
  ```csharp
  await ctx.Response.WriteAsync($"data: {evt}\n\n");
  ```
  This matches how `ChatEndpoints.cs` does it (raw pass-through, no re-serialization).

- [ ] Verify: `cd J:/workspace2/llm/continue_story_4/backend && dotnet build --no-restore`

---

### Task 2: Add tool event callbacks to frontend API

**Dependencies:** None

- [ ] In `frontend/src/lib/api.ts`, update the `chat()` function. Find the `onThinking` callback and add two new callbacks alongside it in the options interface:
  - `onToolStart?: (toolName: string, args: any) => void`
  - `onToolEnd?: (toolName: string, result: any, isError: boolean) => void`

  In the SSE event handler for `message_update`, there's already handling for `thinking_delta`. After it, add handling for `tool_execution_start` and `tool_execution_end`:
  ```typescript
  if (evt.type === 'tool_execution_start') {
    options.onToolStart?.(evt.toolName, evt.args);
  }
  if (evt.type === 'tool_execution_end') {
    options.onToolEnd?.(evt.toolName, evt.result, evt.isError);
  }
  ```

- [ ] Do the same for `knowledgeChat()` — add the same `onToolStart`/`onToolEnd` handling.

---

### Task 3: Render tool execution blocks in ChatPanel.svelte

**Dependencies:** Task 2

- [ ] Add state variables for tracking in-progress tools:
  ```typescript
  let activeTools = $state<Array<{ name: string; args: any; id: number }>>([]);
  let completedTools = $state<Array<{ name: string; args: any; result?: string; isError: boolean; id: number }>>([]);
  let toolIdCounter = 0;
  ```

- [ ] Wire up the new callbacks in the `sendMessage` function where `api.chat()` is called:
  ```typescript
  onToolStart: (toolName, args) => {
    const id = ++toolIdCounter;
    activeTools.push({ name: toolName, args, id });
    completedTools = completedTools.filter(t => true); // trigger reactivity
  },
  onToolEnd: (toolName, result, isError) => {
    const tool = activeTools.find(t => t.name === toolName);
    if (tool) {
      activeTools = activeTools.filter(t => t !== tool);
      let resultStr: string;
      if (typeof result === 'string') resultStr = result.slice(0, 200);
      else resultStr = JSON.stringify(result).slice(0, 200);
      completedTools.push({ ...tool, result: resultStr, isError });
    }
  },
  ```

- [ ] Reset tool state when streaming starts (in `sendMessage`):
  ```typescript
  activeTools = [];
  completedTools = [];
  ```

- [ ] Render the tool blocks in the message template. In the streaming assistant message section, after the thinking block and before/between the streaming text, add:
  ```html
  {#each completedTools as tool (tool.id)}
    <div class="tool-block completed" class:error={tool.isError}>
      <div class="tool-header">
        <span class="tool-icon">{getToolIcon(tool.name)}</span>
        <span class="tool-label">{getToolLabel(tool.name, tool.args)}</span>
        <span class="tool-status">✓</span>
      </div>
      {#if tool.result}
        <details>
          <summary>Result</summary>
          <pre class="tool-result">{tool.result}</pre>
        </details>
      {/if}
    </div>
  {/each}
  {#each activeTools as tool (tool.id)}
    <div class="tool-block active">
      <div class="tool-header">
        <span class="tool-icon spinning">{getToolIcon(tool.name)}</span>
        <span class="tool-label">{getToolLabel(tool.name, tool.args)}</span>
        <span class="tool-status"><span class="spinner"></span></span>
      </div>
    </div>
  {/each}
  ```

- [ ] Create `frontend/src/lib/tool-labels.ts` — the shared abstraction layer:
  ```typescript
  export interface ToolActivity {
    icon: string;
    label: string;
  }

  /** Parse a chapter filename like "ch-003-the-beginning.md" into "Chapter 3: The Beginning" */
  function parseChapterName(path: string): string | null {
    const match = path.match(/ch-(\d+)(?:-(.+))?\.(?:md|scratch\.md)/);
    if (!match) return null;
    const num = parseInt(match[1], 10);
    const slug = match[2];
    if (!slug) return `Chapter ${num}`;
    const title = slug.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
    return `Chapter ${num}: ${title}`;
  }

  /** Parse a wiki path like "wiki/characters/yuki-tanaka.md" into { category, name } */
  function parseWikiPath(path: string): { category: string; name: string } | null {
    const match = path.match(/wiki\/([^/]+)\/(.+)\.md$/);
    if (!match) return null;
    const category = match[1];
    const name = match[2].replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
    return { category, name };
  }

  /** Parse a KB path like "research/japanese-mythology.md" into { category, name } */
  function parseKbPath(path: string): { category: string; name: string } | null {
    const match = path.match(/^([^/]+)\/(.+)\.md$/);
    if (!match) return null;
    const category = match[1];
    const name = match[2].replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
    return { category, name };
  }

  /** Parse a bash command into a human description */
  function parseBashCommand(cmd: string): string | null {
    // ls variants
    if (/^ls\b/.test(cmd)) {
      if (cmd.includes('chapter')) return 'Listing chapters';
      if (cmd.includes('wiki')) return 'Listing wiki entries';
      return 'Browsing files';
    }
    // grep
    const grepMatch = cmd.match(/grep\b.*?["'](.+?)["']/);
    if (grepMatch) return `Searching text for '${grepMatch[1]}'`;
    // cat
    const catMatch = cmd.match(/cat\s+(.+)/);
    if (catMatch) return `Reading ${parseChapterName(catMatch[1]) ?? parseWikiPath(catMatch[1])?.name ?? catMatch[1]}`;
    // mkdir
    if (/^mkdir/.test(cmd)) return 'Creating directory';
    // Default
    return 'Running command';
  }

  /** Extract domain from URL */
  function parseDomain(url: string): string {
    try {
      const u = new URL(url);
      return u.hostname.replace(/^www\./, '');
    } catch {
      return url;
    }
  }

  /** Main function — translates a raw tool call into a user-facing activity description */
  export function describeToolActivity(name: string, args: any): ToolActivity {
    const str = (v: any): string => typeof v === 'string' ? v : JSON.stringify(v) ?? '';

    switch (name) {
      case 'read': {
        const path = str(args.path ?? args);
        const chapter = parseChapterName(path);
        if (chapter) return { icon: '📖', label: `Reading ${chapter}` };
        const wiki = parseWikiPath(path);
        if (wiki) return { icon: '📖', label: `Reading ${wiki.category.slice(0, -1)}: ${wiki.name}` };
        const kb = parseKbPath(path);
        if (kb) return { icon: '📖', label: `Reading: ${kb.name}` };
        return { icon: '📖', label: 'Reading file' };
      }

      case 'write': {
        const path = str(args.path ?? args);
        const isScratch = path.includes('.scratch');
        const chapter = parseChapterName(path);
        if (chapter) return { icon: isScratch ? '✏️' : '📄', label: `${isScratch ? 'Editing' : 'Writing'} ${chapter}` };
        const wiki = parseWikiPath(path);
        if (wiki) return { icon: '📝', label: `Updating ${wiki.category.slice(0, -1)}: ${wiki.name}` };
        const kb = parseKbPath(path);
        if (kb) return { icon: '📝', label: `Writing: ${kb.name}` };
        return { icon: '📝', label: 'Writing file' };
      }

      case 'edit': {
        const path = str(args.path ?? args);
        const chapter = parseChapterName(path);
        if (chapter) return { icon: '✏️', label: `Editing ${chapter}` };
        return { icon: '✏️', label: 'Editing file' };
      }

      case 'bash': {
        const cmd = str(args.command ?? args);
        const desc = parseBashCommand(cmd);
        return { icon: '⌨️', label: desc ?? 'Running command' };
      }

      case 'web_search': {
        const query = str(args.query ?? args);
        return { icon: '🔍', label: `Searching the web for '${query}'` };
      }

      case 'web_fetch': {
        const url = str(args.url ?? args);
        return { icon: '🌐', label: `Fetching ${parseDomain(url)}` };
      }

      default:
        return { icon: '🔧', label: 'Working...' };
    }
  }
  ```

- [ ] In ChatPanel.svelte, import and use `describeToolActivity`:
  ```typescript
  import { describeToolActivity } from '$lib/tool-labels';
  ```

  Then in the template, replace `getToolIcon(tool.name)` and `getToolLabel(tool.name, tool.args)` with:
  ```typescript
  const activity = $derived(describeToolActivity(tool.name, tool.args));
  ```
  And render:
  ```html
  <span class="tool-icon">{activity.icon}</span>
  <span class="tool-label">{activity.label}</span>
  ```

- [ ] Add CSS for tool blocks:
  ```css
  .tool-block {
    margin: 6px 0;
    padding: 6px 10px;
    background: var(--bg-tertiary, #21262d);
    border: 1px solid var(--border, #30363d);
    border-radius: 6px;
    font-size: 12px;
    color: var(--text-secondary, #8b949e);
  }

  .tool-block.active {
    border-color: var(--accent, #6366f1);
  }

  .tool-block.error {
    border-color: #f97583;
  }

  .tool-header {
    display: flex;
    align-items: center;
    gap: 6px;
  }

  .tool-label {
    flex: 1;
    font-family: 'Cascadia Code', 'Fira Code', monospace;
    font-size: 11px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .tool-status {
    font-size: 11px;
  }

  .tool-result {
    margin-top: 6px;
    padding: 6px;
    background: var(--bg-primary, #0d1117);
    border-radius: 4px;
    max-height: 120px;
    overflow-y: auto;
    white-space: pre-wrap;
    font-size: 11px;
  }

  .spinner {
    display: inline-block;
    width: 10px;
    height: 10px;
    border: 2px solid var(--border, #30363d);
    border-top-color: var(--accent, #6366f1);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
  }

  @keyframes spin {
    to { transform: rotate(360deg); }
  }
  ```

  Place this CSS in the same `<style>` block as the existing styles. If `spin` keyframe already exists, skip redefining it.

---

### Task 4: Refactor knowledge page to use ChatPanel component

**Dependencies:** Task 2, Task 3

The knowledge page currently has a full duplicate chat implementation (338 lines). Refactor it to reuse ChatPanel.svelte as a component, then add the knowledge-specific layout around it.

- [ ] Update `ChatPanel.svelte` props to support both book chat and KB chat modes. Add an optional `mode` prop:
  ```typescript
  let {
    slug,
    activeChapterId = null,
    onEditDone,
    onResponseDone,
    mode = 'book',  // 'book' or 'knowledge'
  } = $props();
  ```

- [ ] In ChatPanel.svelte, branch on `mode`:
  - `'book'`: existing behavior — calls `api.chat(slug, ...)`, `api.getChatSession(slug)`, etc.
  - `'knowledge'`: calls `api.knowledgeChat(msg, sessionId)`, `api.getKnowledgeChatSession()`, `api.createNewKnowledgeChatSession()`, clear via `DELETE /api/knowledge/chat/session/{sessionId}`

- [ ] Replace the entire chat section in `frontend/src/routes/knowledge/+page.svelte` with:
  ```html
  <ChatPanel mode="knowledge" slug="__shared__" />
  ```
  Remove all the duplicated chat state, SSE handling, sendMessage, clearHistory, etc. The knowledge page should only contain the header + KnowledgePanel + the ChatPanel component.

- [ ] Verify both pages build and work.

---

### Task 5: Build, deploy, verify

**Dependencies:** All preceding tasks

- [ ] `cd J:/workspace2/llm/continue_story_4 && docker compose build && docker compose up -d`
- [ ] Open a book chat, send a message that triggers tool use (e.g., "read chapter 1"). Verify:
  - Tool blocks appear inline during execution with spinner
  - Completed tools show ✓ with collapsible result
  - Streaming text still works
  - Thinking section still works
- [ ] Open `/knowledge`, send a research message. Verify same tool display works + streaming text now visible during response.
