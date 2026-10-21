// Shared helper imported by several handlers.
export function versionFor(runVersion: string, sha: string): string {
  return `${runVersion}+${sha.slice(0, 7)}`;
}
export const releaseName = (env: string) => `olve-pipelines-${env}`;
