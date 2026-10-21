import { client, numberMap } from '../api.js';
import { parseManifest, type Manifest } from './manifest.js';

export type LoadedVersion = {
  number: number;
  manifest: Manifest | null;
  manifestText: string;
  stepVersions: Record<string, number>;
  modelVersions: Record<string, number>;
  commitSha: string | null;
  createdAt: Date | null;
};

export type Activation =
  | { kind: 'active'; version: number; since: Date | null }
  | { kind: 'draining'; from: number; to: number; cancelRunning: boolean; since: Date | null }
  | { kind: 'none' };

/** Normalise the API's polymorphic `PipelineActivation` (`active` / `draining`). */
export function toActivation(a: unknown): Activation {
  const o = (a ?? {}) as Record<string, unknown>;
  const since = (o.since as Date | undefined) ?? null;
  if (o.type === 'active') return { kind: 'active', version: Number(o.versionNumber), since };
  if (o.type === 'draining')
    return {
      kind: 'draining',
      from: Number(o.fromVersionNumber),
      to: Number(o.toVersionNumber),
      cancelRunning: Boolean(o.cancelRunning),
      since,
    };
  return { kind: 'none' };
}

/** The version new runs use (the switch target while draining). */
export function currentVersion(a: Activation): number | null {
  return a.kind === 'active' ? a.version : a.kind === 'draining' ? a.to : null;
}

// Versions are immutable, so a per-session cache is safe.
const cache = new Map<string, Promise<LoadedVersion | null>>();

export function loadVersion(pipelineId: string, number: number): Promise<LoadedVersion | null> {
  const key = `${pipelineId}#${number}`;
  let p = cache.get(key);
  if (!p) {
    p = client.api.pipelines
      .byId(pipelineId)
      .versions.byNumber(number)
      .get()
      .then((v) =>
        v
          ? {
              number: v.number ?? number,
              manifest: parseManifest(v.manifest),
              manifestText: v.manifest ?? '',
              stepVersions: numberMap(v.stepVersions),
              modelVersions: numberMap(v.modelVersions),
              commitSha: v.commitSha ?? null,
              createdAt: v.createdAt ?? null,
            }
          : null,
      );
    p.catch(() => cache.delete(key));
    cache.set(key, p);
  }
  return p;
}
