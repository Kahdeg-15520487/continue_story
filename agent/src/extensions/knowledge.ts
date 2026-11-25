import { defineExtension, section } from "@earendil-works/pi-durable";
import { KB_INSTRUCTIONS } from "../prompts.js";
import { splitAtHeadings } from "./sections.js";

const parts = splitAtHeadings(KB_INSTRUCTIONS, "kb");

/** The shared-knowledge prompt, rendered from the registry. */
export const KnowledgeExtension = defineExtension({
  name: "knowledge",
  sections: parts.map((part) => section(part.key, () => part.body, { tag: false })),
});

export const KB_SECTION_PARTS = parts;
