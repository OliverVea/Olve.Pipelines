import type { RunChanges } from '@olve/api';

/** Whether a run's changes have anything to show next to the run itself. */
export function hasChanges(
  c: RunChanges | null | undefined,
  versionNumber: number,
  configSnapshot: number | null | undefined,
): c is RunChanges {
  if (!c || c.sinceNumber == null) return false;
  return (
    !!c.commits?.length ||
    !!c.commitsError ||
    c.sinceVersionNumber !== versionNumber ||
    (c.sinceConfigSnapshot ?? null) !== (configSnapshot ?? null)
  );
}

/**
 * Which run each commit came with: the run whose push SHA it is, labelled on that commit only, since the commits
 * after it (older, up to the next run's push) came with the same push. Only runs after `sinceNumber` and up to
 * `number` count; a SHA several of them pushed goes to the newest.
 */
export function commitRuns(
  commits: readonly { sha: string }[],
  runs: readonly { number: number; pushSha?: string | null }[],
  sinceNumber: number,
  number: number,
): (number | null)[] {
  const bySha = new Map<string, number>();
  for (const r of runs) {
    if (!r.pushSha || r.number <= sinceNumber || r.number > number) continue;
    const sha = r.pushSha.toLowerCase();
    bySha.set(sha, Math.max(bySha.get(sha) ?? 0, r.number));
  }
  return commits.map((c) => bySha.get(c.sha.toLowerCase()) ?? null);
}
