import { ORIGIN } from "./brands.ts";

/**
 * The file that called the function calling this one, from the stack. A tail call drops its caller's
 * frame, so this can name a module further up; the build only uses it to choose what to hash.
 */
export function callerFile(): string | undefined {
  const files = (new Error().stack ?? "").split("\n").slice(1).map((f) => f.match(FRAME)?.[1]);
  const own = files[0];
  const callee = files[1];
  const file = files.slice(2).find((f) => f && f !== own && f !== callee) ?? undefined;
  return file?.startsWith("file://") ? new URL(file).pathname : file;
}
const FRAME = /\(?((?:file:\/\/)?\/[^()]*?):\d+(?::\d+)?\)?\s*$/;

/** The modules that defined and implemented a step (where `step` and `implement` were called). */
export function originsOf(v: unknown): string[] {
  const o = (v as Record<symbol, unknown>)[ORIGIN];
  return Array.isArray(o) ? o.filter((x): x is string => typeof x === "string") : [];
}
