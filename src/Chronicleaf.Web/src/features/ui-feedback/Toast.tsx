import { useSyncExternalStore } from 'react';

/**
 * The one toast surface for form outcomes (WEB-FEED-001).
 *
 * The store is module-level on purpose: a toast shown by a save that
 * navigates away (project, milestone, client create) must survive the
 * navigation, and state held by the saving panel would unmount with the
 * form. Panels announce outcomes through `showToast`; the single
 * `<ToastRegion/>` in the product shell renders whatever is current.
 *
 * One toast at a time, latest replaces: a second show cancels the first
 * timer and drops the first toast. The toast leaves on its own 4000ms after
 * the latest show, and the dismiss button clears it sooner.
 */

export type ToastKind = 'ok' | 'error';

export interface ToastValue {
  readonly id: number;
  readonly message: string;
  readonly kind: ToastKind;
}

/** How long a toast stays current after the latest show, in milliseconds. */
export const TOAST_DISMISS_MS = 4000;

let current: ToastValue | null = null;
let nextId = 1;
let timer: ReturnType<typeof setTimeout> | null = null;
const listeners = new Set<() => void>();

function emit(): void {
  for (const listener of listeners) {
    listener();
  }
}

function clearTimer(): void {
  if (timer !== null) {
    clearTimeout(timer);
    timer = null;
  }
}

/** Shows a toast, replacing whatever is current and restarting the clock. */
export function showToast(message: string, kind: ToastKind): void {
  clearTimer();
  current = { id: nextId++, message, kind };
  timer = setTimeout(() => {
    timer = null;
    current = null;
    emit();
  }, TOAST_DISMISS_MS);
  emit();
}

/** Clears the current toast, if any. The auto-dismiss timer is cancelled. */
export function dismissToast(): void {
  clearTimer();

  if (current !== null) {
    current = null;
    emit();
  }
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);

  return () => {
    listeners.delete(listener);
  };
}

function snapshot(): ToastValue | null {
  return current;
}

/** The current toast, re-rendering the caller on every show or dismiss. */
export function useToast(): {
  readonly toast: ToastValue | null;
  readonly showToast: (message: string, kind: ToastKind) => void;
  readonly dismissToast: () => void;
} {
  const toast = useSyncExternalStore(subscribe, snapshot, snapshot);

  return { toast, showToast, dismissToast };
}

/**
 * The single toast region. Always present (a live region that mounts late
 * misses the announcement), fixed top-center; the toast itself renders only
 * while one is current.
 */
export function ToastRegion() {
  const { toast, dismissToast: dismiss } = useToast();

  return (
    <div className="toast-region" role="status" aria-live="polite" data-testid="toast-region">
      {toast !== null ? (
        <div
          className={toast.kind === 'error' ? 'toast toast--error' : 'toast'}
          data-testid="toast"
        >
          <span data-testid="toast-message">{toast.message}</span>
          <button
            type="button"
            className="toast__dismiss"
            aria-label="Dismiss"
            onClick={dismiss}
            data-testid="toast-dismiss"
          >
            Dismiss
          </button>
        </div>
      ) : null}
    </div>
  );
}
