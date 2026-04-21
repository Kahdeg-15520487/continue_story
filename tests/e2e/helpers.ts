import http from 'http';
import { test as base, expect } from '@playwright/test';

function apiRequest(method: string, path: string, body?: any, contentType?: string): Promise<any> {
  return new Promise((resolve, reject) => {
    const options = {
      hostname: 'localhost',
      port: 5000,
      path: `/api${path}`,
      method,
      headers: {} as Record<string, string>,
    };

    let postData: Buffer | undefined;
    if (body) {
      if (contentType === 'multipart/form-data') {
        // Raw multipart body
        postData = body as Buffer;
        options.headers['Content-Type'] = `multipart/form-data; boundary=----FormBoundary`;
      } else {
        postData = Buffer.from(JSON.stringify(body));
        options.headers['Content-Type'] = 'application/json';
        options.headers['Content-Length'] = String(postData.length);
      }
    }

    const req = http.request(options, (res) => {
      const chunks: Buffer[] = [];
      res.on('data', (chunk) => chunks.push(chunk));
      res.on('end', () => {
        const text = Buffer.concat(chunks).toString();
        try {
          resolve({ status: res.statusCode!, data: JSON.parse(text) });
        } catch {
          resolve({ status: res.statusCode!, data: text });
        }
      });
    });

    req.on('error', reject);
    if (postData) req.write(postData);
    req.end();
  });
}

// Build multipart form data manually
function buildMultipart(fieldName: string, fileName: string, content: Buffer): Buffer {
  const boundary = '----FormBoundary';
  const header = `--${boundary}\r\nContent-Disposition: form-data; name="${fieldName}"; filename="${fileName}"\r\nContent-Type: text/plain\r\n\r\n`;
  const footer = `\r\n--${boundary}--\r\n`;
  return Buffer.concat([
    Buffer.from(header),
    content,
    Buffer.from(footer),
  ]);
}

async function waitForBookStatus(slug: string, status: string, timeout = 600_000) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    try {
      const { data } = await apiRequest('GET', `/books/${slug}`);
      if (data.status === status) return data;
      if (data.status === 'error') throw new Error(`Book error: ${data.errorMessage}`);
    } catch (e: any) {
      if (e.message?.startsWith('Book error:')) throw e;
    }
    await new Promise(r => setTimeout(r, 3000));
  }
  throw new Error(`Timeout waiting for '${status}' on book '${slug}'`);
}

async function createAndProcessBook(title: string, content: Buffer) {
  // Create
  const { data: book } = await apiRequest('POST', '/books', { title });
  const slug = book.slug;

  // Upload
  const multipart = buildMultipart('file', 'test.txt', content);
  await new Promise<void>((resolve, reject) => {
    const options = {
      hostname: 'localhost',
      port: 5000,
      path: `/api/books/${slug}/upload`,
      method: 'POST',
      headers: {
        'Content-Type': 'multipart/form-data; boundary=----FormBoundary',
        'Content-Length': String(multipart.length),
      },
    };
    const req = http.request(options, (res) => {
      res.on('data', () => {});
      res.on('end', () => resolve());
    });
    req.on('error', reject);
    req.write(multipart);
    req.end();
  });

  // Wait for full pipeline
  await waitForBookStatus(slug, 'lore-ready');
  return slug;
}

const test = base.extend({});

export { test, expect, createAndProcessBook };
