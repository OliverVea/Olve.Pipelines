import type { Envelope } from './execute.ts';

/**
 * Masks secret values in text: a stream a line at a time, so a value split across two writes is
 * still found; a multi-line value (a key file) line by line.
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
    for (const line of (value ?? '').split(/\r\n|\r|\n/))
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
      if (pending.length > MAX_PENDING) {
        // Keep enough back that a value cut here is whole in what follows.
        const keep = Math.max(0, (patterns[0]?.length ?? 1) - 1);
        write(maskText(pending.slice(0, pending.length - keep), patterns));
        pending = pending.slice(pending.length - keep);
      }
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
  if (envelope.links)
    envelope = {
      ...envelope,
      links: envelope.links.map((l) => ({
        ...l,
        url: maskText(l.url, patterns),
        ...(l.label !== undefined ? { label: maskText(l.label, patterns) } : {}),
      })),
    };
  if (envelope.status === 'failed')
    return {
      ...envelope,
      error: { ...envelope.error, message: maskText(envelope.error.message, patterns) },
    };
  if (envelope.status === 'skipped')
    return { ...envelope, reason: maskText(envelope.reason, patterns) };
  return envelope;
}
