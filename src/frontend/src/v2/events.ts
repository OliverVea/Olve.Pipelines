import type { ReactiveController, ReactiveControllerHost } from 'lit';
import { changeEvents, isNotice, reloadsAll, type ChangeNotice } from '@olve/client';
import { api } from '../api.js';

export type { ChangeNotice };

interface Listener {
  matches(notice: ChangeNotice): boolean;
  /** Something it shows may have changed (a matching notice, or a (re)connect that may have missed some). */
  changed(): void;
}

const RETRY_MS = [1_000, 5_000, 15_000, 30_000];

/**
 * One change-event stream per tab, open while a view listens and the tab is visible. Every `hello`
 * (first connect or reconnect) and `resync` tells all listeners to reload: nothing is replayed, so
 * that is what covers events missed before or between connections.
 */
class ChangeStream {
  private readonly listeners = new Set<Listener>();
  private abort?: AbortController;
  private retryTimer?: ReturnType<typeof setTimeout>;
  private failures = 0;

  constructor() {
    document.addEventListener('visibilitychange', () =>
      document.hidden ? this.close() : this.open(),
    );
  }

  add(listener: Listener): () => void {
    this.listeners.add(listener);
    this.open();
    return () => {
      this.listeners.delete(listener);
      if (!this.listeners.size) this.close();
    };
  }

  private open() {
    if (this.abort || this.retryTimer || document.hidden || !this.listeners.size) return;
    const abort = new AbortController();
    this.abort = abort;
    void this.read(abort);
  }

  private close() {
    clearTimeout(this.retryTimer);
    this.retryTimer = undefined;
    this.abort?.abort();
    this.abort = undefined;
  }

  private async read(abort: AbortController) {
    try {
      for await (const event of changeEvents(api, abort.signal)) {
        this.failures = 0;
        if (reloadsAll(event)) this.all();
        else if (isNotice(event)) this.dispatch(event);
      }
    } catch {
      if (abort.signal.aborted) return;
      this.failures++;
    }
    if (this.abort !== abort) return;
    this.abort = undefined;
    const delay = RETRY_MS[Math.min(this.failures, RETRY_MS.length - 1)];
    this.retryTimer = setTimeout(() => {
      this.retryTimer = undefined;
      this.open();
    }, delay);
  }

  private all() {
    for (const l of this.listeners) l.changed();
  }

  private dispatch(notice: ChangeNotice) {
    for (const l of this.listeners) if (l.matches(notice)) l.changed();
  }
}

let stream: ChangeStream | undefined;

/**
 * Reloads its host when a matching change notice arrives (debounced), when the stream (re)connects,
 * and every `fallbackMs` while the tab is visible as a safety net.
 */
export class LiveController implements ReactiveController {
  private readonly reload: () => void;
  private readonly matches: (notice: ChangeNotice) => boolean;
  private readonly fallbackMs: number;
  private readonly debounceMs: number;
  private off?: () => void;
  private debounce?: ReturnType<typeof setTimeout>;
  private fallback?: ReturnType<typeof setInterval>;

  constructor(
    host: ReactiveControllerHost,
    reload: () => void,
    matches: (notice: ChangeNotice) => boolean,
    fallbackMs = 60_000,
    debounceMs = 250,
  ) {
    this.reload = reload;
    this.matches = matches;
    this.fallbackMs = fallbackMs;
    this.debounceMs = debounceMs;
    host.addController(this);
  }

  hostConnected() {
    stream ??= new ChangeStream();
    this.off = stream.add({
      matches: (n) => this.matches(n),
      changed: () => this.schedule(),
    });
    this.fallback = setInterval(() => {
      if (!document.hidden) this.reload();
    }, this.fallbackMs);
  }

  hostDisconnected() {
    this.off?.();
    clearTimeout(this.debounce);
    clearInterval(this.fallback);
  }

  private schedule() {
    clearTimeout(this.debounce);
    this.debounce = setTimeout(this.reload, this.debounceMs);
  }
}

/** Notices about one pipeline, optionally only of some types. */
export const forPipeline =
  (pipelineId: () => string, ...types: ChangeNotice['type'][]) =>
  (n: ChangeNotice) =>
    n.pipelineId === pipelineId() && (!types.length || types.includes(n.type));
