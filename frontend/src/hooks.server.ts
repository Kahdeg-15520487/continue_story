import type { Handle } from '@sveltejs/kit';

/**
 * Never let HTML be cached at the edge or in the browser.
 *
 * Hashed /_app/immutable/* assets stay immutable, but the app shell must be
 * revalidated on every navigation or a deploy can be masked by a stale copy
 * (Cloudflare cached `/` and kept serving the pre-deploy shell).
 */
export const handle: Handle = async ({ event, resolve }) => {
  const response = await resolve(event);
  const contentType = response.headers.get('content-type') ?? '';

  if (contentType.includes('text/html')) {
    response.headers.set('cache-control', 'no-store, no-cache, must-revalidate');
    response.headers.set('pragma', 'no-cache');
    response.headers.set('expires', '0');
  }

  return response;
};
