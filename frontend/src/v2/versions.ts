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
