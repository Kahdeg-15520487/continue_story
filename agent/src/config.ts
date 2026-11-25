import { dirname } from "path";

export const PORT = parseInt(process.env.PORT || "3001", 10);
export const LIBRARY_ROOT = process.env.LIBRARY_ROOT || "/library";
export const DURABLE_DB = process.env.DURABLE_DB || "/data/durable.sqlite";
export const SEARXNG_URL = process.env.SEARXNG_URL || "http://searxng:8080";
export const PI_MODEL = process.env.PI_MODEL || "deepseek/deepseek-flash";
export const SHARED_SLUG = "__shared__";

const [provider, ...rest] = PI_MODEL.split("/");
export const MODEL = { provider, modelId: rest.join("/") };

export function durableDir(): string {
  return dirname(DURABLE_DB);
}
