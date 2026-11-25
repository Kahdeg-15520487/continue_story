export type PromptPart = { key: string; body: string };

/** Split a prompt at its top-level `## ` headings, preserving the exact text. */
export function splitAtHeadings(text: string, prefix: string): PromptPart[] {
  const starts: number[] = [];
  if (text.startsWith("## ")) starts.push(0);
  const re = /\n(?=## )/g;
  let match: RegExpExecArray | null;
  while ((match = re.exec(text)) !== null) starts.push(match.index + 1);
  if (starts.length === 0) return [{ key: `${prefix}-prompt`, body: text }];

  return starts.map((start, index) => {
    const end = index + 1 < starts.length ? starts[index + 1] : text.length;
    const body = text.slice(start, end);
    const heading = body.match(/^## (.+)/)?.[1] ?? `part-${index}`;
    return { key: `${prefix}-${slug(heading)}`, body };
  });
}

function slug(value: string): string {
  return value
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}
