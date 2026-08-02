// Toast store — Svelte 5 runes module. Import with the full `.svelte.ts` extension.
export type ToastKind = 'error' | 'success' | 'info';

export interface Toast {
  id: number;
  kind: ToastKind;
  message: string;
}

export let toasts: Toast[] = $state([]);
let nextId = 1;

export function toast(kind: ToastKind, message: string, duration = 4000): number {
  const id = nextId++;
  toasts.push({ id, kind, message });
  if (duration > 0) {
    setTimeout(() => dismiss(id), duration);
  }
  return id;
}

export const toastError = (message: string, duration?: number): number => toast('error', message, duration);
export const toastSuccess = (message: string, duration?: number): number => toast('success', message, duration);
export const toastInfo = (message: string, duration?: number): number => toast('info', message, duration);

export function dismiss(id: number): void {
  // Mutate in place — Svelte disallows reassigning exported module state
  const idx = toasts.findIndex(t => t.id === id);
  if (idx >= 0) toasts.splice(idx, 1);
}
