import { BACKGROUND_CONTEXT } from "@earendil-works/chord/context";
import { openHarness } from "./harness.js";
import { startServer } from "./server.js";
import { PORT } from "./config.js";

const harness = await openHarness();
const server = startServer(harness);

console.log(`[agent] durable harness open, server on :${PORT}`);

let shuttingDown = false;
async function shutdown(signal: string): Promise<void> {
  if (shuttingDown) return;
  shuttingDown = true;
  console.log(`[agent] ${signal} — closing`);
  server.close();
  try {
    await harness.close(BACKGROUND_CONTEXT);
  } catch (err: any) {
    console.error("[agent] close failed:", err?.message ?? err);
  }
  process.exit(0);
}

process.on("SIGTERM", () => void shutdown("SIGTERM"));
process.on("SIGINT", () => void shutdown("SIGINT"));
