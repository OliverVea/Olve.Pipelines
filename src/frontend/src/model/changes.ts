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
