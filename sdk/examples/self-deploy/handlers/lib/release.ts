// Shared helper imported by several handlers (counts towards their handler hashes).
export const releaseName = (env: string) => `olve-pipelines-${env}`;
export const versionFor = (stamp: string, sha: string) => `${stamp}+${sha.slice(0, 7)}`;
