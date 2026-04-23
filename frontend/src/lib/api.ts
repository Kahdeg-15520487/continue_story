import type { BookSummary, BookDetail, BookContent, CreateBookRequest, WikiIndex, UploadResult, ConversionStatus, ChatHistoryMessage } from './types';

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

  // Lore / Wiki
  getWikiIndex: (slug: string) =>
    request<WikiIndex>(`/books/${slug}/lore`),

  getWikiEntity: (slug: string, category: string, entity: string) =>
    request<{ file: string; content: string }>(`/books/${slug}/lore/${category}/${encodeURIComponent(entity)}`),

  getWikiSummary: (slug: string) =>
    request<{ file: string; content: string }>(`/books/${slug}/lore/summary`),

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

  saveChatMessage: (slug: string, role: string, content: string, thinking?: string) =>
    request<{ id: number }>(`/books/${slug}/chat`, {
      method: 'POST',
      body: JSON.stringify({ role, content, thinking: thinking || null }),
    }),

  clearChatHistory: (slug: string, sessionId?: string) =>
    request<{ cleared: boolean }>(`/books/${slug}/chat${sessionId ? `?sessionId=${encodeURIComponent(sessionId)}` : ''}`, {
      method: 'DELETE',
    }),

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
    options?: { activeChapterId?: string | null; sessionId?: string | null; onEditDone?: (chapterId: string) => void; onSessionInfo?: (sessionId: string) => void },
  ): AbortController {
    const controller = new AbortController();
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
          onDone();
          return;
        }
        const reader = res.body?.getReader();
        if (!reader) return;
        const decoder = new TextDecoder();
        let sseBuffer = '';
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          sseBuffer += decoder.decode(value, { stream: true });
          const messages = sseBuffer.split('\n\n');
          sseBuffer = messages.pop()!;
          for (const msg of messages) {
            for (const line of msg.split('\n')) {
              if (line.startsWith('data: ')) {
                try {
                  const evt = JSON.parse(line.slice(6));
                  if (evt.type === 'agent_end') {
                    onDone();
                    return;
                  } else if (evt.type === 'session_info') {
                    if (evt.sessionId) {
                      options?.onSessionInfo?.(evt.sessionId);
                    }
                  } else if (evt.type === 'edit_done') {
                    if (evt.chapterId) {
                      options?.onEditDone?.(evt.chapterId);
                    }
                  } else if (evt.type === 'message_update') {
                    const delta = evt.assistantMessageEvent;
                    if (delta?.type === 'text_delta') {
                      onChunk(delta.delta);
                    } else if (delta?.type === 'thinking_delta') {
                      onThinking?.(delta.delta);
                    }
                  } else if (evt.type === 'error') {
                    onError?.(evt.message || evt.error || 'Unknown error');
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
          onError?.(err.message || 'Network error');
        }
        onDone();
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
          onDone('');
          return;
        }
        const reader = res.body?.getReader();
        if (!reader) return;
        const decoder = new TextDecoder();
        let sseBuffer = '';
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          sseBuffer += decoder.decode(value, { stream: true });
          const messages = sseBuffer.split('\n\n');
          sseBuffer = messages.pop()!;
          for (const msg of messages) {
            for (const line of msg.split('\n')) {
              if (line.startsWith('data: ')) {
                try {
                  const evt = JSON.parse(line.slice(6));
                  if (evt.type === 'edit_done') {
                    onDone(evt.scratchPath || '');
                    return;
                  } else if (evt.type === 'message_update') {
                    const delta = evt.assistantMessageEvent;
                    if (delta?.type === 'text_delta') {
                      onChunk(delta.delta);
                    } else if (delta?.type === 'thinking_delta') {
                      onThinking?.(delta.delta);
                    }
                  } else if (evt.type === 'error') {
                    onError?.(evt.message || evt.error || 'Unknown error');
                  }
                } catch {
                  // Ignore parse errors
                }
              }
            }
          }
        }
        onDone('');
      })
      .catch((err) => {
        if (err.name !== 'AbortError') {
          onError?.(err.message || 'Network error');
        }
        onDone('');
      });
    return controller;
  },

  acceptInlineEdit: (slug: string, id: string) =>
    request<{ accepted: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/inline-edit/accept`, { method: 'POST' }),

  rejectInlineEdit: (slug: string, id: string) =>
    request<{ rejected: boolean }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/inline-edit/reject`, { method: 'POST' }),

  getScratchContent: (slug: string, id: string) =>
    request<{ content: string }>(`/books/${slug}/chapters/${encodeURIComponent(id)}/scratch`),

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
