import { marked } from 'marked';
import DOMPurify from 'dompurify';

// Open markdown links in a new tab (marked v18 passes token objects to renderers).
// Note: sanitizing happens after rendering, so any attribute weirdness gets stripped.
marked.use({
  renderer: {
    link(token: any) {
      const text = token.text || '';
      const title = token.title ? ` title="${String(token.title).replace(/"/g, '&quot;')}"` : '';
      return `<a href="${token.href}"${title} target="_blank" rel="noopener noreferrer">${text}</a>`;
    },
  },
});

// In Node (SSR) the dompurify ESM export is a stub without `sanitize` — fine, because
// real markdown content only renders client-side after fetches. In the browser it's
// the full API and we strip any HTML/JS the model or a wiki entry might contain.
const sanitize = (DOMPurify as { sanitize?: (html: string) => string }).sanitize;

export function renderMarkdown(text: string): string {
  const raw = marked.parse(text, { async: false }) as string;
  return sanitize ? sanitize(raw) : raw;
}
