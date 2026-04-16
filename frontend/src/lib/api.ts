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
