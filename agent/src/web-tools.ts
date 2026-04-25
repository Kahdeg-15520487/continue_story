import { Type } from "@sinclair/typebox";
import { defineTool } from "@mariozechner/pi-coding-agent";

const SEARXNG_URL = process.env.SEARXNG_URL || "http://host.docker.internal:8888";

// ---------- shared HTTP helper ----------

function fetchUrl(urlStr: string, timeout = 15000): Promise<{ status: number; body: string }> {
  return new Promise((resolve, reject) => {
    const mod = urlStr.startsWith("https") ? require("https") : require("http");
    const { URL } = require("url");
    const url = new URL(urlStr);
    mod.get(
      url,
      { timeout, headers: { "User-Agent": "Mozilla/5.0 (compatible; StoryAgent/1.0)" } },
      (res: any) => {
        // Follow redirects
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          return fetchUrl(res.headers.location, timeout).then(resolve, reject);
        }
        const chunks: Buffer[] = [];
        res.on("data", (c: Buffer) => chunks.push(c));
        res.on("end", () => resolve({ status: res.statusCode, body: Buffer.concat(chunks).toString("utf-8") }));
      },
    ).on("error", reject)
      .on("timeout", function (this: any) { this.destroy(); reject(new Error("Request timed out")); });
  });
}

function htmlToText(html: string): string {
  return html
    .replace(/<script[\s\S]*?<\/script>/gi, "")
    .replace(/<style[\s\S]*?<\/style>/gi, "")
    .replace(/<nav[\s\S]*?<\/nav>/gi, "")
    .replace(/<header[\s\S]*?<\/header>/gi, "")
    .replace(/<footer[\s\S]*?<\/footer>/gi, "")
    .replace(/<[^>]+>/g, " ")
    .replace(/&nbsp;/g, " ")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/\s+/g, " ")
    .trim();
}

// ---------- web_search ----------

export const webSearchTool = defineTool({
  name: "web_search",
  label: "Web Search",
  description:
    "Search the web using SearXNG. Returns a list of results with title, URL, and snippet. " +
    "Use this to research franchises, games, novels, characters, settings, or any topic before writing. " +
    "For deeper research, follow up with web_fetch on promising results.",
  promptSnippet: "web_search({query, categories?, max_results?}) — search the web for information",
  parameters: Type.Object({
    query: Type.String({ description: "Search query" }),
    categories: Type.Optional(Type.String({ description: "Search category: general, news, images, it, science, files, music, videos, social media. Default: general" })),
    language: Type.Optional(Type.String({ description: "Language code, e.g. en, ja, ko, vi. Default: en" })),
    max_results: Type.Optional(Type.Number({ description: "Maximum results to return. Default: 10" })),
  }),
  async execute(_toolCallId, params, _signal, _onUpdate, _ctx) {
    const max = params.max_results || 10;
    const searchParams = new URLSearchParams({ q: params.query, format: "json" });
    if (params.categories) searchParams.set("categories", params.categories);
    if (params.language) searchParams.set("language", params.language);

    const url = `${SEARXNG_URL}/search?${searchParams.toString()}`;
    let response;
    try {
      response = await fetchUrl(url);
    } catch (err: any) {
      return { content: [{ type: "text" as const, text: `Search failed: ${err.message}` }], isError: true };
    }

    if (response.status !== 200) {
      return { content: [{ type: "text" as const, text: `Search returned HTTP ${response.status}` }], isError: true };
    }

    let data: any;
    try {
      data = JSON.parse(response.body);
    } catch {
      return { content: [{ type: "text" as const, text: "Failed to parse search results" }], isError: true };
    }

    const results = (data.results || []).slice(0, max);
    if (results.length === 0) {
      return { content: [{ type: "text" as const, text: "No results found." }] };
    }

    const text = results
      .map((r: any, i: number) => {
        let line = `[${i + 1}] ${r.title}\n    URL: ${r.url}`;
        if (r.content) line += `\n    ${r.content}`;
        return line;
      })
      .join("\n\n");

    return { content: [{ type: "text" as const, text }] };
  },
});

// ---------- web_fetch ----------

export const webFetchTool = defineTool({
  name: "web_fetch",
  label: "Web Fetch",
  description:
    "Fetch a web page and extract its text content. Use after web_search to read full pages. " +
    "Good for reading wiki pages, articles, reviews, and reference material about franchises/stories.",
  promptSnippet: "web_fetch({url, max_length?}) — fetch and read a web page's content",
  parameters: Type.Object({
    url: Type.String({ description: "URL to fetch" }),
    max_length: Type.Optional(Type.Number({ description: "Max characters to return. Default: 10000" })),
  }),
  async execute(_toolCallId, params, _signal, _onUpdate, _ctx) {
    const maxLen = params.max_length || 10000;
    let response;
    try {
      response = await fetchUrl(params.url);
    } catch (err: any) {
      return { content: [{ type: "text" as const, text: `Fetch failed: ${err.message}` }], isError: true };
    }

    if (response.status !== 200) {
      return { content: [{ type: "text" as const, text: `HTTP ${response.status}` }], isError: true };
    }

    const text = htmlToText(response.body);
    const truncated = text.slice(0, maxLen);
    const suffix = text.length > maxLen ? `\n\n... [truncated, ${text.length} chars total]` : "";

    return { content: [{ type: "text" as const, text: truncated + suffix }] };
  },
});
