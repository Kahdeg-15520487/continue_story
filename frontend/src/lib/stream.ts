export type StreamHandlers = {
  onChunk?: (text: string) => void;
  onThinking?: (text: string) => void;
  onToolStart?: (name: string, args: any) => void;
  onToolEnd?: (name: string, result: any, isError: boolean) => void;
  onDone?: () => void;
  onError?: (err: string) => void;
  onView?: (view: any) => void;
  onSessionInfo?: (id: string) => void;
  onEditDone?: (chapterId: string) => void;
};

/**
 * Adapts pi-durable `value` frames to the legacy callbacks the existing chat
 * UI uses. The durable shape itself (`pi.live.generation`, `pi.live.tools`,
 * `run`, `runFailure`) is the source of truth.
 */
export class StreamAdapter {
  private partialText = '';
  private partialThinking = '';
  private toolStatus = new Map<string, string>();
  private sawRun = false;
  private finished = false;

  constructor(private handlers: StreamHandlers) {}

  handle(evt: any): void {
    if (!evt || typeof evt !== 'object') return;

    if (evt.kind === 'end') {
      this.finish();
      return;
    }
    if (evt.kind === 'value') {
      this.handleValue(evt.value);
      return;
    }

    if (evt.type === 'session_info' && evt.sessionId) this.handlers.onSessionInfo?.(evt.sessionId);
    else if (evt.type === 'edit_done' && evt.chapterId) this.handlers.onEditDone?.(evt.chapterId);
    else if (evt.type === 'error') this.handlers.onError?.(evt.message || evt.error || 'Unknown error');
  }

  private finish(): void {
    if (this.finished) return;
    this.finished = true;
    this.handlers.onDone?.();
  }

  private handleValue(value: any): void {
    if (!value) return;
    this.handlers.onView?.(value);

    const live = value.docs?.['pi.live'];
    const message = live?.generation?.message;

    const partial = textOf(message);
    const committed = newestAssistantText(value.entries);
    const text = committed.length >= partial.length ? committed : partial;
    if (text.startsWith(this.partialText)) {
      const delta = text.slice(this.partialText.length);
      if (delta) this.handlers.onChunk?.(delta);
    } else if (text) {
      // A retry replaced the partial; the UI only appends, so send the whole text.
      this.handlers.onChunk?.(text);
    }
    this.partialText = text;

    const partialThinking = thinkingOf(message);
    const committedThinking = newestAssistantThinking(value.entries);
    const thinking = committedThinking.length >= partialThinking.length ? committedThinking : partialThinking;
    if (thinking.startsWith(this.partialThinking)) {
      const delta = thinking.slice(this.partialThinking.length);
      if (delta) this.handlers.onThinking?.(delta);
    } else if (thinking) {
      this.handlers.onThinking?.(thinking);
    }
    this.partialThinking = thinking;

    const calls = toolCalls(value.entries);
    for (const tool of live?.tools ?? []) {
      const previous = this.toolStatus.get(tool.callId);
      if (!previous && (tool.status === 'running' || tool.status === 'pending')) {
        this.handlers.onToolStart?.(tool.name, calls.get(tool.callId) ?? {});
      }
      if (previous && previous !== 'done' && tool.status === 'done') {
        this.handlers.onToolEnd?.(tool.name, tool.output ?? tool.details ?? '', Boolean(tool.isError));
      }
      this.toolStatus.set(tool.callId, tool.status);
    }

    if (live?.run) this.sawRun = true;
    if (live?.runFailure) {
      this.handlers.onError?.(runFailureText(live.runFailure));
      this.finish();
      return;
    }
    if (this.sawRun && !live?.run) this.finish();
  }
}


function newestAssistantText(entries: any[] | undefined): string {
  let text = '';
  for (const entry of entries ?? []) {
    if (entry?.kind !== 'pi.assistant') continue;
    for (const message of entry.model ?? []) {
      const value = textOf(message);
      if (value) text = value;
    }
  }
  return text;
}

function newestAssistantThinking(entries: any[] | undefined): string {
  let thinking = '';
  for (const entry of entries ?? []) {
    if (entry?.kind !== 'pi.assistant') continue;
    for (const message of entry.model ?? []) {
      const value = thinkingOf(message);
      if (value) thinking = value;
    }
  }
  return thinking;
}

function textOf(message: any): string {
  const content = message?.content;
  if (typeof content === 'string') return content;
  if (!Array.isArray(content)) return '';
  return content
    .filter((block: any) => block?.type === 'text' && typeof block.text === 'string')
    .map((block: any) => block.text)
    .join('');
}

function thinkingOf(message: any): string {
  const content = message?.content;
  if (!Array.isArray(content)) return '';
  return content
    .filter((block: any) => block?.type === 'thinking')
    .map((block: any) => block.thinking ?? block.text ?? '')
    .join('');
}

function toolCalls(entries: any[] | undefined): Map<string, any> {
  const calls = new Map<string, any>();
  for (const entry of entries ?? []) {
    for (const message of entry.model ?? []) {
      for (const block of message.content ?? []) {
        if (block?.type === 'toolCall' && block.id) calls.set(block.id, block.arguments ?? {});
      }
    }
  }
  return calls;
}

function runFailureText(failure: any): string {
  if (typeof failure === 'string') return failure;
  return failure?.reason ?? failure?.message ?? 'Run ended without an answer';
}
