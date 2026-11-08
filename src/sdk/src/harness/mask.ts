import { format } from 'node:util';
import type { Envelope } from './execute.ts';

/**
 * Masks the step's declared secret values in what it writes to stdout and stderr. Output is masked a
 * line at a time, so a value split across two writes is still found; a multi-line value (a key file)
 * is masked line by line.
 */

export const MASK = '***';
/** Shorter values (and lines of multi-line values) are left alone: they would mask ordinary text. */
export const MIN_MASKED_LENGTH = 4;
/** A line longer than this is masked and written without waiting for its end. */
const MAX_PENDING = 64 * 1024;

/** The strings to mask for these secret values, longest first. */
export function maskPatterns(values: readonly (string | undefined)[]): string[] {
  const patterns = new Set<string>();
  for (const value of values)
    for (const line of (value ?? '').split(/\r?\n/))
      if (line.trim().length >= MIN_MASKED_LENGTH) patterns.add(line.trim());
  return [...patterns].sort((a, b) => b.length - a.length);
}

export function maskText(text: string, patterns: readonly string[]): string {
  let out = text;
  for (const p of patterns) if (out.includes(p)) out = out.split(p).join(MASK);
  return out;
}

/** A writer that masks complete lines and holds back the unfinished one until it ends or `flush`. */
export function lineMasker(patterns: readonly string[], write: (text: string) => void) {
  const decoder = new TextDecoder();
  let pending = '';
  return {
    write(chunk: string | Uint8Array) {
      pending +=
        typeof chunk === 'string' ? chunk : decoder.decode(chunk, { stream: true });
      const end = Math.max(pending.lastIndexOf('\n'), pending.lastIndexOf('\r'));
      if (end >= 0) {
        write(maskText(pending.slice(0, end + 1), patterns));
        pending = pending.slice(end + 1);
      }
      if (pending.length > MAX_PENDING) this.flush();
    },
    flush() {
      pending += decoder.decode();
      if (pending) write(maskText(pending, patterns));
      pending = '';
    },
  };
}

/** The envelope with its failure message or skip reason masked; outputs are data and stay as they are. */
export function maskEnvelope(envelope: Envelope, patterns: readonly string[]): Envelope {
  if (envelope.status === 'failed')
    return {
      ...envelope,
      error: { ...envelope.error, message: maskText(envelope.error.message, patterns) },
    };
  if (envelope.status === 'skipped')
    return { ...envelope, reason: maskText(envelope.reason, patterns) };
  return envelope;
}

type Write = typeof process.stdout.write;

/**
 * Routes process.stdout and process.stderr through maskers until the returned function is called,
 * which flushes them and restores the streams.
 */
export function installMasking(patterns: readonly string[]): () => void {
  const streams = [process.stdout, process.stderr];
  const originals = streams.map((s) => s.write.bind(s) as Write);
  const maskers = originals.map((original) =>
    lineMasker(patterns, (text) => void original(text)),
  );
  streams.forEach((s, i) => {
    s.write = ((chunk: string | Uint8Array, a?: unknown, b?: unknown) => {
      maskers[i]!.write(chunk);
      const callback = typeof a === 'function' ? a : typeof b === 'function' ? b : null;
      if (callback) queueMicrotask(() => (callback as () => void)());
      return true;
    }) as Write;
  });
  // Bun's console writes natively, past process.stdout.write.
  const methods = ['log', 'info', 'debug', 'warn', 'error'] as const;
  const consoleOriginals = methods.map((m) => console[m]);
  methods.forEach((m) => {
    const stream = m === 'warn' || m === 'error' ? process.stderr : process.stdout;
    console[m] = (...args: unknown[]) => void stream.write(format(...args) + '\n');
  });
  return () => {
    maskers.forEach((m) => m.flush());
    streams.forEach((s, i) => (s.write = originals[i]!));
    methods.forEach((m, i) => (console[m] = consoleOriginals[i]!));
  };
}
