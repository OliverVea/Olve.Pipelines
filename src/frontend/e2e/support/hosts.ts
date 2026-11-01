import { existsSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

export type TestTarget = {
  apiUrl: string;
  clusterId: string;
  kubernetes: {
    server: string;
    token: string;
    caCertificate: string;
    namespace: string;
  };
};

const descriptors = fileURLToPath(
  new URL('../../../../artifacts/real-system/', import.meta.url),
);

function listUrls(): string[] {
  const split = (text: string) =>
    text
      .split(/[,\r\n]/)
      .map((url) => url.trim())
      .filter(Boolean);
  const { TEST_TARGETS: listed, TEST_TARGETS_FILE: file } = process.env;
  if (listed?.trim()) return split(listed);
  if (file?.trim()) return split(readFileSync(file, 'utf8'));
  return ['http://localhost:5000'];
}

/** The hosts to use, picked the way the real-system tests pick theirs. */
export function loadTargets(): TestTarget[] {
  return listUrls().map((url) => {
    const { hostname, port } = new URL(url);
    const path = `${descriptors}${hostname}-${port}.json`;
    if (!existsSync(path))
      throw new Error(
        `No host at ${url}: ${path} does not exist. Start one with tools/real-system/host.sh up.`,
      );
    return JSON.parse(readFileSync(path, 'utf8')) as TestTarget;
  });
}
