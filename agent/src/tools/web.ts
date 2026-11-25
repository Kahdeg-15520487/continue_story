import http from "http";
import https from "https";
import { defineExtension, defineTool } from "@earendil-works/pi-durable";
import { num, obj, opt, str } from "@earendil-works/pi-schema";
import { SEARXNG_URL } from "../config.js";

// ---------- shared HTTP helper ----------

function fetchUrl(urlStr: string, timeout = 15000): Promise<{ status: number; body: string }> {
  return new Promise((resolve, reject) => {
    const url = new URL(urlStr);
    const mod = url.protocol === "https:" ? https : http;
    mod
      .get(
        url,
        { timeout, headers: { "User-Agent": "Mozilla/5.0 (compatible; StoryAgent/1.0)" } },
        (res) => {
          if (res.statusCode! >= 300 && res.statusCode! < 400 && res.headers.location) {
            return fetchUrl(res.headers.location, timeout).then(resolve, reject);
          }
          const chunks: Buffer[] = [];
          res.on("data", (c: Buffer) => chunks.push(c));
          res.on("end", () => resolve({ status: res.statusCode!, body: Buffer.concat(chunks).toString("utf-8") }));
        },
      )
      .on("error", reject)
      .on("timeout", function (this: http.ClientRequest) {
        this.destroy();
        reject(new Error("Request timed out"));
      });
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

type WebDetails = { isError: boolean };

function ok(text: string) {
  return { output: [{ type: "text" as const, text }], details: { isError: false } as WebDetails, isError: false };
}

function fail(text: string) {
  return { output: [{ type: "text" as const, text }], details: { isError: true } as WebDetails, isError: true };
}

// ---------- web_search ----------

const webSearchParameters = obj({
  query: str(),
  categories: opt(str()),
  language: opt(str()),
  max_results: opt(num()),
});

export const webSearchTool = defineTool<typeof webSearchParameters, WebDetails>({
  name: "web_search",
  description:
    "Search the web using SearXNG. Returns a list of results with title, URL, and snippet. " +
    "Use this to research franchises, games, novels, characters, settings, or any topic before writing. " +
    "For deeper research, follow up with web_fetch on promising results.",
  replay: "safe",
  parameters: webSearchParameters,
  async execute(args) {
    const max = args.max_results || 10;
    const searchParams = new URLSearchParams({ q: args.query, format: "json" });
    if (args.categories) searchParams.set("categories", args.categories);
    if (args.language) searchParams.set("language", args.language);

    let response;
    try {
      response = await fetchUrl(`${SEARXNG_URL}/search?${searchParams.toString()}`);
    } catch (err: any) {
      return fail(`Search failed: ${err.message}`);
    }
    if (response.status !== 200) return fail(`Search returned HTTP ${response.status}`);

    let data: any;
    try {
      data = JSON.parse(response.body);
    } catch {
      return fail("Failed to parse search results");
    }

    const results = (data.results || []).slice(0, max);
    if (results.length === 0) return ok("No results found.");

    const text = results
      .map((r: any, i: number) => {
        let line = `[${i + 1}] ${r.title}\n    URL: ${r.url}`;
        if (r.content) line += `\n    ${r.content}`;
        return line;
      })
      .join("\n\n");

    return ok(text);
  },
});

// ---------- web_fetch ----------

const webFetchParameters = obj({
  url: str(),
  max_length: opt(num()),
});

export const webFetchTool = defineTool<typeof webFetchParameters, WebDetails>({
  name: "web_fetch",
  description:
    "Fetch a web page and extract its text content. Use after web_search to read full pages. " +
    "Good for reading wiki pages, articles, reviews, and reference material about franchises/stories.",
  replay: "safe",
  parameters: webFetchParameters,
  async execute(args) {
    const maxLen = args.max_length || 10000;
    let response;
    try {
      response = await fetchUrl(args.url);
    } catch (err: any) {
      return fail(`Fetch failed: ${err.message}`);
    }
    if (response.status !== 200) return fail(`HTTP ${response.status}`);

    const text = htmlToText(response.body);
    const truncated = text.slice(0, maxLen);
    const suffix = text.length > maxLen ? `\n\n... [truncated, ${text.length} chars total]` : "";
    return ok(truncated + suffix);
  },
});

export const WebTools = defineExtension({ name: "web", tools: [webSearchTool, webFetchTool] });
