import { canonicalJson } from "./canonical.ts";
import { sha256Hex } from "./hash.ts";

/** Identity of the harness compiled into a binary. A Bun or SDK bump legitimately bumps every handler. */
export interface HarnessInfo {
  /** Bun version (runtime compiled into the binary). */
  bun: string;
  /** @olivervea/pipelines package version. */
  sdk: string;
  /** `sha256:` hash over the SDK runtime sources and the zod version. */
  sdkHash: string;
}

/** Custom step: hash of the handler bundle (contract files + SDK external) combined with the harness. */
export function composeHandlerHash(bundleHash: string, harness: HarnessInfo): string {
  return "sha256:" + sha256Hex(canonicalJson({ bundle: bundleHash, harness }));
}

/** A step that carries its implementation: kind + canonical settings + harness, and the hash of the code that implements the kind. */
export function libraryHandlerHash(kind: string, config: unknown, harness: HarnessInfo, code?: string): string {
  return "sha256:" + sha256Hex(canonicalJson({ library: kind, config, harness, ...(code ? { code } : {}) }));
}
