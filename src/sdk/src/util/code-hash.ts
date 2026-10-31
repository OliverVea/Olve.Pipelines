import { canonicalJson } from './canonical.ts';
import { sha256Hex } from './hash.ts';

/** Identity of the harness compiled into a binary. */
export interface HarnessInfo {
  /** Bun version (runtime compiled into the binary). */
  bun: string;
  /** @olivervea/pipelines package version. */
  sdk: string;
  /** `sha256:` hash over the SDK runtime sources and the zod version. */
  sdkHash: string;
}

/** The hash of a whole build: its code bundled with the SDK external, combined with the harness. */
export function composeCodeHash(bundleHash: string, harness: HarnessInfo): string {
  return 'sha256:' + sha256Hex(canonicalJson({ bundle: bundleHash, harness }));
}
