export interface ToolActivity {
  icon: string;
  label: string;
}

/** Parse a chapter filename like "ch-003-the-beginning.md" into "Chapter 3: The Beginning" */
function parseChapterName(path: string): string | null {
  const match = path.match(/ch-(\d+)(?:-(.+))?\.(?:md|scratch\.md)/);
  if (!match) return null;
  const num = parseInt(match[1], 10);
  const slug = match[2];
  if (!slug) return `Chapter ${num}`;
  const title = slug.replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
  return `Chapter ${num}: ${title}`;
}

/** Parse a wiki path like "wiki/characters/yuki-tanaka.md" into { category, name } */
function parseWikiPath(path: string): { category: string; name: string } | null {
  const match = path.match(/wiki\/([^/]+)\/(.+)\.md$/);
  if (!match) return null;
  const category = match[1];
  const name = match[2].replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
  return { category, name };
}

/** Parse a KB path like "research/japanese-mythology.md" into { category, name } */
function parseKbPath(path: string): { category: string; name: string } | null {
  const match = path.match(/^([^/]+)\/(.+)\.md$/);
  if (!match) return null;
  const category = match[1];
  const name = match[2].replace(/-/g, ' ').replace(/\b\w/g, c => c.toUpperCase());
  return { category, name };
}

/** Parse a bash command into a human description */
function parseBashCommand(cmd: string): string | null {
  // ls variants
  if (/^ls\b/.test(cmd)) {
    if (cmd.includes('chapter')) return 'Listing chapters';
    if (cmd.includes('wiki')) return 'Listing wiki entries';
    return 'Browsing files';
  }
  // grep
  const grepMatch = cmd.match(/grep\b.*?["'](.+?)["']/);
  if (grepMatch) return `Searching text for '${grepMatch[1]}'`;
  // cat
  const catMatch = cmd.match(/cat\s+(.+)/);
  if (catMatch) return `Reading ${parseChapterName(catMatch[1]) ?? parseWikiPath(catMatch[1])?.name ?? catMatch[1]}`;
  // mkdir
  if (/^mkdir/.test(cmd)) return 'Creating directory';
  // Default
  return 'Running command';
}

/** Extract domain from URL */
function parseDomain(url: string): string {
  try {
    const u = new URL(url);
    return u.hostname.replace(/^www\./, '');
  } catch {
    return url;
  }
}

/** Main function — translates a raw tool call into a user-facing activity description */
export function describeToolActivity(name: string, args: any): ToolActivity {
  const str = (v: any): string => typeof v === 'string' ? v : JSON.stringify(v) ?? '';

  switch (name) {
    case 'read': {
      const path = str(args.path ?? args);
      const chapter = parseChapterName(path);
      if (chapter) return { icon: '📖', label: `Reading ${chapter}` };
      const wiki = parseWikiPath(path);
      if (wiki) return { icon: '📖', label: `Reading ${wiki.category.slice(0, -1)}: ${wiki.name}` };
      const kb = parseKbPath(path);
      if (kb) return { icon: '📖', label: `Reading: ${kb.name}` };
      return { icon: '📖', label: 'Reading file' };
    }

    case 'write': {
      const path = str(args.path ?? args);
      const isScratch = path.includes('.scratch');
      const chapter = parseChapterName(path);
      if (chapter) return { icon: isScratch ? '✏️' : '📄', label: `${isScratch ? 'Editing' : 'Writing'} ${chapter}` };
      const wiki = parseWikiPath(path);
      if (wiki) return { icon: '📝', label: `Updating ${wiki.category.slice(0, -1)}: ${wiki.name}` };
      const kb = parseKbPath(path);
      if (kb) return { icon: '📝', label: `Writing: ${kb.name}` };
      return { icon: '📝', label: 'Writing file' };
    }

    case 'edit': {
      const path = str(args.path ?? args);
      const chapter = parseChapterName(path);
      if (chapter) return { icon: '✏️', label: `Editing ${chapter}` };
      return { icon: '✏️', label: 'Editing file' };
    }

    case 'bash': {
      const cmd = str(args.command ?? args);
      const desc = parseBashCommand(cmd);
      return { icon: '⌨️', label: desc ?? 'Running command' };
    }

    case 'web_search': {
      const query = str(args.query ?? args);
      return { icon: '🔍', label: `Searching the web for '${query}'` };
    }

    case 'web_fetch': {
      const url = str(args.url ?? args);
      return { icon: '🌐', label: `Fetching ${parseDomain(url)}` };
    }

    default:
      return { icon: '🔧', label: 'Working...' };
  }
}
