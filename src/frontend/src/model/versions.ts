import { versionsEffective, versionsGet } from '@olve/api';
import { api } from '../api/client.js';
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
  | {
      kind: 'draining';
      from: number;
      to: number;
      cancelRunning: boolean;
      since: Date | null;
    }
  | { kind: 'none' };

/** Normalise the API's polymorphic `PipelineActivation` (`active` / `draining`). */
export function toActivation(a: unknown): Activation {
  const o = (a ?? {}) as Record<string, unknown>;
  const since = o.since ? new Date(o.since as string | Date) : null;
  if (o.type === 'active')
    return { kind: 'active', version: Number(o.versionNumber), since };
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

export function loadVersion(
  pipelineId: string,
  number: number,
): Promise<LoadedVersion | null> {
  const key = `${pipelineId}#${number}`;
  let p = cache.get(key);
  if (!p) {
    p = versionsGet({
      client: api,
      throwOnError: true,
      path: { pipelineId, number },
    }).then(({ data: v }) => ({
      number: v.number,
      manifest: parseManifest(v.manifest),
      manifestText: v.manifest,
      stepVersions: v.stepVersions,
      modelVersions: v.modelVersions,
      commitSha: v.commitSha,
      createdAt: new Date(v.createdAt),
    }));
    p.catch(() => cache.delete(key));
    cache.set(key, p);
  }
  return p;
}

export type EffectivePipeline =
  | {
      ok: true;
      manifest: Manifest | null;
      /** Step name → the select cases that picked it ("target.beta.kind=inCluster"). */
      cases: Record<string, string[]>;
    }
  | { ok: false; reason: string };

/**
 * The pipeline a version runs with a config snapshot (default: the pipeline's latest): each `select`
 * resolved to its case. Versions without a select are returned as they are.
 */
export async function loadEffective(
  pipelineId: string,
  version: LoadedVersion,
  snapshot?: number | null,
): Promise<EffectivePipeline> {
  const hasSelect = (version.manifest?.stages ?? []).some(function walk(s): boolean {
    return 'select' in s || ('stages' in s && s.stages.some(walk));
  });
  if (!hasSelect) return { ok: true, manifest: version.manifest, cases: {} };
  try {
    const { data: r } = await versionsEffective({
      client: api,
      throwOnError: true,
      path: { pipelineId, number: version.number },
      query: snapshot ? { snapshot } : undefined,
    });
    return { ok: true, manifest: parseManifest(r.manifest), cases: r.cases };
  } catch (e) {
    return {
      ok: false,
      reason:
        (e as { errors?: { message?: string }[] })?.errors?.[0]?.message ??
        (e as Error).message,
    };
  }
}
