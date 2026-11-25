import { defineExtension, section } from "@earendil-works/pi-durable";
import { STORY_INSTRUCTIONS } from "../prompts.js";
import { splitAtHeadings } from "./sections.js";

const parts = splitAtHeadings(STORY_INSTRUCTIONS, "story");

/** The story prompt, rendered from the registry instead of stored per conversation. */
export const StoryExtension = defineExtension({
  name: "story",
  sections: parts.map((part) => section(part.key, () => part.body, { tag: false })),
});

export const STORY_SECTION_PARTS = parts;
