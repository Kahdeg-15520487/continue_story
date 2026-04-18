import type { BookSummary, BookDetail, BookContent, CreateBookRequest, LoreFiles, LoreContent, UploadResult, ConversionStatus } from './types';

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
  chat(
    bookSlug: string,
    message: string,
    onChunk: (data: string) => void,
    onDone: () => void,
    onError?: (err: string) => void,
  ): AbortController {
    const controller = new AbortController();
    fetch(`${BASE}/chat`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bookSlug, message }),
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
                  } else if (evt.type === 'message_update') {
                    const delta = evt.assistantMessageEvent;
                    if (delta?.type === 'text_delta') {
                      onChunk(delta.delta);
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
