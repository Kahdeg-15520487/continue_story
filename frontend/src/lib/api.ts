import type { BookSummary, BookDetail, BookContent, CreateBookRequest, WikiIndex, UploadResult, ConversionStatus, ChatHistoryMessage } from './types';
import { createTranscript } from './transcript';
import { StreamAdapter } from './stream';

const BASE = '/api';

function buildSearchParams(q?: string, tags?: string[]): string {
  const params = new URLSearchParams();
  if (q) params.set('q', q);
  if (tags && tags.length > 0) params.set('tags', tags.join(','));
  const str = params.toString();
  return str ? `?${str}` : '';
}

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

async function readSse(res: Response, onFrame: (evt: any) => void): Promise<void> {
  const reader = res.body?.getReader();
  if (!reader) return;
  const decoder = new TextDecoder();
  let buffer = '';
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    const messages = buffer.split('\n\n');
    buffer = messages.pop()!;
    for (const msg of messages) {
      for (const line of msg.split('\n')) {
        if (!line.startsWith('data: ')) continue;
        try {
          onFrame(JSON.parse(line.slice(6)));
        } catch {
          // Ignore parse errors
        }
      }
    }
  }
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

  updateBook: (slug: string, data: { title?: string; author?: string }) =>
    request<BookDetail>(`/books/${slug}`, {
      method: 'PATCH',
      body: JSON.stringify(data),
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

  // Lore / Wiki
  getWikiIndex: (slug: string) =>
    request<WikiIndex>(`/books/${slug}/lore`),

  getWikiEntity: (slug: string, category: string, entity: string) =>
    request<{ file: string; content: string }>(`/books/${slug}/lore/${category}/${encodeURIComponent(entity)}`),

  getWikiSummary: (slug: string) =>
    request<{ file: string; content: string }>(`/books/${slug}/lore/summary`),

  saveWikiEntity: (slug: string, category: string, entity: string, content: string) =>
    request<{ saved: boolean; file: string }>(`/books/${slug}/lore/${category}/${encodeURIComponent(entity)}`, {
      method: 'PUT',
      body: JSON.stringify({ content }),
    }),

  triggerLoreGeneration: (slug: string) =>
    request<{ jobId: string; status: string }>(`/books/${slug}/lore`, {
      method: 'POST',
    }),
  triggerChapterSplit: (slug: string) =>
    request<{ jobId: string; status: string }>(`/books/${slug}/split`, {
      method: 'POST',
    }),

  // Chat history
  getChatHistory: (slug: string, limit = 100, sessionId?: string) =>
    request<ChatHistoryMessage[]>(`/books/${slug}/chat?limit=${limit}${sessionId ? `&sessionId=${encodeURIComponent(sessionId)}` : ''}`),

  saveChatMessage: (slug: string, role: string, content: string, thinking?: string, sessionId?: string) =>
    request<{ id: number }>(`/books/${slug}/chat`, {
      method: 'POST',
      body: JSON.stringify({ role, content, thinking: thinking || null, sessionId: sessionId || null }),
    }),

  clearChatHistory: (slug: string, sessionId?: string) =>
    request<{ cleared: boolean }>(`/books/${slug}/chat${sessionId ? `?sessionId=${encodeURIComponent(sessionId)}` : ''}`, {
      method: 'DELETE',
    }),

  abortChat: (slug: string) =>
    request<{ aborted: boolean; lastUserMessage?: string }>(`/books/${slug}/chat/abort`, {
      method: 'POST',
    }),

  abortKnowledgeChat: (sessionId: string) =>
    request<{ aborted: boolean }>('/knowledge/chat/abort', {
      method: 'POST',
      body: JSON.stringify({ sessionId }),
    }),

  getLastUserMessage: (slug: string) =>
    request<{ lastUserMessage: string }>(`/books/${slug}/chat/last-message`),

  // Chapters
  listChapters: (slug: string) =>
    request<Array<{ id: string; number: number; title: string; wordCount: number; fileName: string }>>(`/books/${slug}/chapters`),

  getChapter: (slug: string, id: string) =>
    request<{ id: string; title: string; content: string }>(`/books/${slug}/chapters/${encodeURIComponent(id)}`),

  saveChapter: (slug: string, id: string, content: string) =>
    request<{ saved: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify({ content }),
    }),

  insertChapter: (slug: string, title: string, afterChapterId?: string) =>
    request<{ id: string; number: number; title: string; wordCount: number; fileName: string }>(`/books/${slug}/chapters`, {
      method: 'POST',
      body: JSON.stringify({ title, afterChapterId }),
    }),

  deleteChapter: (slug: string, id: string) =>
    request<{ deleted: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}`, {
      method: 'DELETE',
    }),

  reorderChapters: (slug: string, orderedIds: string[]) =>
    request<{ reordered: boolean }>(`/books/${slug}/chapters/reorder`, {
      method: 'POST',
      body: JSON.stringify({ orderedIds }),
    }),

  regenerateTitles: (slug: string) =>
    request<{ queued: boolean; jobId: string }>(`/books/${slug}/chapters/regenerate-titles`, {
      method: 'POST',
    }),

  // Agent session management
  getChatSession: (slug: string) =>
    request<{ sessionId: string }>(`/books/${slug}/chat/session`),

  createNewChatSession: (slug: string) =>
    request<{ sessionId: string }>(`/books/${slug}/chat/session/new`, { method: 'POST' }),

  listChatSessions: (slug: string) =>
    request<{ sessions: { id: string; bookSlug: string; age: string; idle: string; tokenCount: number }[] }>(`/books/${slug}/chat/sessions`),

  // Agent tasks
  getAgentTasks: (slug: string) =>
    request<Array<{ id: number; taskType: string; description: string; status: string; errorMessage: string | null; updatedAt: string }>>(`/agent/tasks/${slug}`),

  // Chat (SSE)
  chat(
    bookSlug: string,
    message: string,
    onChunk: (data: string) => void,
    onDone: () => void,
    onError?: (err: string) => void,
    onThinking?: (text: string) => void,
    onToolStart?: (toolName: string, args: any) => void,
    onToolEnd?: (toolName: string, result: any, isError: boolean) => void,
    options?: { activeChapterId?: string | null; sessionId?: string | null; onEditDone?: (chapterId: string) => void; onSessionInfo?: (sessionId: string) => void },
  ): AbortController {
    const controller = new AbortController();
    const transcript = createTranscript(bookSlug);
    let finished = false;
    const done = () => {
      if (finished) return;
      finished = true;
      onDone();
    };
    const adapter = new StreamAdapter({
      onChunk,
      onThinking,
      onToolStart,
      onToolEnd,
      onSessionInfo: options?.onSessionInfo,
      onEditDone: options?.onEditDone,
      onError,
      onDone: done,
      onView: (view) => { transcript.merge(view); },
    });

    fetch(`${BASE}/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        bookSlug,
        message,
        activeChapterId: options?.activeChapterId ?? null,
        sessionId: options?.sessionId ?? null,
      }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) {
          const err = await res.json().catch(() => ({ error: res.statusText }));
          onError?.(err.error || `HTTP ${res.status}`);
          done();
          return;
        }
        await readSse(res, (evt) => adapter.handle(evt));
        done();
      })
      .catch((err) => {
        if (err.name !== 'AbortError') onError?.(err.message || 'Network error');
        done();
      });
    return controller;
  },

  // Inline Edit (SSE)
  inlineEdit(
    bookSlug: string,
    chapterId: string,
    selectedText: string,
    instruction: string,
    onChunk: (delta: string) => void,
    onDone: (scratchPath: string) => void,
    onError?: (err: string) => void,
    onThinking?: (text: string) => void,
  ): AbortController {
    const controller = new AbortController();
    const transcript = createTranscript(bookSlug);
    let finished = false;
    let scratchPath = '';
    const done = (path: string) => {
      if (finished) return;
      finished = true;
      onDone(path);
    };
    const adapter = new StreamAdapter({
      onChunk,
      onThinking,
      onError,
      onDone: () => {},
      onView: (view) => { transcript.merge(view); },
    });

    fetch(`${BASE}/books/${bookSlug}/chapters/${encodeURIComponent(chapterId)}/inline-edit`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ selectedText, instruction }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) {
          const err = await res.json().catch(() => ({ error: res.statusText }));
          onError?.(err.error || `HTTP ${res.status}`);
          done('');
          return;
        }
        await readSse(res, (evt) => {
          if (evt?.type === 'edit_done') {
            scratchPath = evt.scratchPath || '';
            done(scratchPath);
            return;
          }
          adapter.handle(evt);
        });
        done(scratchPath);
      })
      .catch((err) => {
        if (err.name !== 'AbortError') onError?.(err.message || 'Network error');
        done(scratchPath);
      });
    return controller;
  },

  acceptInlineEdit: (slug: string, id: string) =>
    request<{ accepted: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/inline-edit/accept`, { method: 'POST' }),

  rejectInlineEdit: (slug: string, id: string) =>
    request<{ rejected: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/inline-edit/reject`, { method: 'POST' }),

  getScratchContent: (slug: string, id: string) =>
    request<{ content: string }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/scratch`),

  // ── Knowledge Base ──────────────────────────────────────────────

  getKnowledgeIndex: () =>
    request<{ categories: Array<{ name: string; entries: Array<{ file: string; title: string; tags: string[] }> }>; tags: string[] }>('/knowledge'),

  getKnowledgeEntry: (category: string, entry: string) =>
    request<{ file: string; content: string }>(`/knowledge/${encodeURIComponent(category)}/${encodeURIComponent(entry)}`),

  saveKnowledgeEntry: (category: string, entry: string, content: string) =>
    request<{ saved: boolean }>(`/knowledge/${encodeURIComponent(category)}/${encodeURIComponent(entry)}`, {
      method: 'PUT',
      body: JSON.stringify({ content }),
    }),

  createKnowledgeCategory: (name: string) =>
    request<{ created: boolean; name: string }>('/knowledge/categories', {
      method: 'POST',
      body: JSON.stringify({ name }),
    }),

  deleteKnowledgeEntry: (category: string, entry: string) =>
    request<{ deleted: boolean }>(`/knowledge/${encodeURIComponent(category)}/${encodeURIComponent(entry)}`, {
      method: 'DELETE',
    }),

  deleteKnowledgeCategory: (category: string) =>
    request<{ deleted: boolean }>(`/knowledge/${encodeURIComponent(category)}`, {
      method: 'DELETE',
    }),

  searchKnowledge: (query?: string, tags?: string[]) =>
    request<{ results: Array<{ category: string; file: string; title: string; tags: string[] }> }>(`/knowledge/search${buildSearchParams(query, tags)}`),

  getKnowledgeChatSession: () =>
    request<{ sessionId: string; bookSlug: string; messageCount: number }>('/knowledge/chat/session'),

  createNewKnowledgeChatSession: () =>
    request<{ sessionId: string; bookSlug: string; messageCount: number }>('/knowledge/chat/session/new', {
      method: 'POST',
    }),

  knowledgeChat(
    message: string,
    sessionId: string,
    onChunk: (data: string) => void,
    onDone: () => void,
    onError?: (err: string) => void,
    onThinking?: (text: string) => void,
    onToolStart?: (toolName: string, args: any) => void,
    onToolEnd?: (toolName: string, result: any, isError: boolean) => void,
  ): AbortController {
    const controller = new AbortController();
    const transcript = createTranscript('__shared__');
    let finished = false;
    const done = () => {
      if (finished) return;
      finished = true;
      onDone();
    };
    const adapter = new StreamAdapter({
      onChunk,
      onThinking,
      onToolStart,
      onToolEnd,
      onError,
      onDone: done,
      onView: (view) => { transcript.merge(view); },
    });

    fetch(`${BASE}/knowledge/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message, sessionId }),
      signal: controller.signal,
    })
      .then(async (res) => {
        if (!res.ok) {
          const err = await res.json().catch(() => ({ error: res.statusText }));
          onError?.(err.error || `HTTP ${res.status}`);
          done();
          return;
        }
        await readSse(res, (evt) => adapter.handle(evt));
        done();
      })
      .catch((err) => {
        if (err.name !== 'AbortError') onError?.(err.message || 'Network error');
        done();
      });
    return controller;
  },

  // LLM provider settings
  getLlmSettings: () => request<any>('/settings/llm'),

  saveLlmProvider: (data: {
    id: string;
    name?: string;
    kind?: string;
    baseUrl?: string;
    api?: string;
    apiKey?: string;
    models?: Array<{ id: string; name?: string }>;
  }) =>
    request<{ provider: any }>('/settings/llm/providers', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  deleteLlmProvider: (id: string) =>
    request<{ deleted: boolean }>(`/settings/llm/providers/${encodeURIComponent(id)}`, { method: 'DELETE' }),

  testLlmProvider: (id: string) =>
    request<any>(`/settings/llm/providers/${encodeURIComponent(id)}/test`, { method: 'POST' }),

  setLlmDefault: (data: { provider: string; modelId: string; thinkingLevel?: string }) =>
    request<any>('/settings/llm/default', {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  // Conversion status
  getConversionStatus: (slug: string) =>
    request<ConversionStatus>(`/books/${slug}/upload/status`),

  // Upload file for a book
  upload(
    slug: string,
    file: File,
    onProgress?: (percent: number) => void
  ): Promise<UploadResult> {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest();
      const formData = new FormData();
      formData.append('file', file);

      xhr.upload.addEventListener('progress', (e) => {
        if (e.lengthComputable && onProgress) {
          onProgress(Math.round((e.loaded / e.total) * 100));
        }
      });

      xhr.addEventListener('load', () => {
        if (xhr.status >= 200 && xhr.status < 300) {
          try {
            resolve(JSON.parse(xhr.responseText));
          } catch {
            reject(new Error('Invalid response'));
          }
        } else {
          try {
            const err = JSON.parse(xhr.responseText);
            reject(new Error(err.error || `Upload failed (${xhr.status})`));
          } catch {
            reject(new Error(`Upload failed (${xhr.status})`));
          }
        }
      });

      xhr.addEventListener('error', () => reject(new Error('Network error')));
      xhr.addEventListener('abort', () => reject(new Error('Upload cancelled')));

      xhr.open('POST', `${BASE}/books/${slug}/upload`);
      xhr.send(formData);
    });
  },
};
