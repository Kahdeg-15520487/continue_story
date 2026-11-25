import { ConversationTranscript } from '@earendil-works/pi-durable';
import type { TranscriptView } from '@earendil-works/pi-durable';

export type { TranscriptView };

/**
 * The built-in client transcript. `merge(view)` keeps entries the server's
 * window dropped and produces gap/"load earlier" items; `resume` is the window
 * to reconnect with.
 */
export function createTranscript(slug: string): ConversationTranscript {
  return new ConversationTranscript(async (query, limit) => {
    const params = new URLSearchParams({ limit: String(limit) });
    if (query.minEntryId !== undefined) params.set('after', String(query.minEntryId));
    if (query.maxEntryId !== undefined) params.set('before', String(query.maxEntryId));
    if (query.order) params.set('order', query.order);

    const res = await fetch(`/api/books/${encodeURIComponent(slug)}/chat/entries?${params.toString()}`);
    if (!res.ok) throw new Error(`Failed to load transcript entries (${res.status})`);
    return res.json();
  });
}
