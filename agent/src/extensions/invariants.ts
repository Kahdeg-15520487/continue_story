import { ToolTask, defineExtension, hook } from "@earendil-works/pi-durable";
import type { ToolHookCall } from "@earendil-works/pi-durable";

const IMMUTABLE = new Set(["book.md", "book.org.md"]);

function normalize(path: string): string {
  return path.replace(/\\/g, "/");
}

function baseName(path: string): string {
  return normalize(path).split("/").pop() ?? "";
}

function hasTraversal(path: string): boolean {
  return normalize(path).split("/").includes("..");
}

function pathArgument(call: ToolHookCall): string | undefined {
  const value = call.arguments?.path;
  return typeof value === "string" ? value : undefined;
}

/**
 * Structural invariants the prompt used to only ask for. Phase 1.5 ships this
 * conservative: immutability and traversal are enforced; absolute paths are
 * logged (a later phase can tighten once cwd is available to the hook).
 */
export const InvariantsExtension = defineExtension({
  name: "invariants",
  hooks: [
    hook(ToolTask, {
      beforeTool(call) {
        if (call.name !== "write" && call.name !== "edit") return undefined;
        const path = pathArgument(call);
        if (!path) return undefined;
        if (IMMUTABLE.has(baseName(path))) {
          return { block: `${path} is immutable; chapters live in chapters/*.md` };
        }
        if (hasTraversal(path)) {
          return { block: `${path} escapes the book workspace` };
        }
        if (normalize(path).startsWith("/")) {
          console.log(`[invariants] absolute path allowed: ${path}`);
        }
        return undefined;
      },
      afterTool(call) {
        if (call.name !== "write" && call.name !== "edit") return undefined;
        const path = pathArgument(call);
        if (path && baseName(path).endsWith(".scratch.md")) {
          console.log(`[invariants] scratch write: ${path}`);
        }
        return undefined;
      },
    }),
  ],
});
