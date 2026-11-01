// Layout of a pipeline directory and the identity of the SDK building it.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { sha256Hex } from '../util/hash.ts';
import type { HarnessInfo } from '../util/code-hash.ts';

export const SDK_ROOT = resolve(import.meta.dir, '../..');

export interface Project {
  dir: string;
  pipelineFile: string;
}

export class ProjectError extends Error {
  override name = 'ProjectError';
}

export async function loadProject(dirArg: string): Promise<Project> {
  const dir = resolve(dirArg);
  if (!existsSync(dir) || !statSync(dir).isDirectory())
    throw new ProjectError(`${dirArg} is not a directory`);
  return { dir, pipelineFile: entryOf(dirArg, dir) };
}

/** The file `main` in the package's package.json names. */
function entryOf(dirArg: string, dir: string): string {
  const pkg = join(dir, 'package.json');
  const main = existsSync(pkg)
    ? (JSON.parse(readFileSync(pkg, 'utf8')) as { main?: string }).main
    : undefined;
  if (!main)
    throw new ProjectError(
      `${join(dirArg, 'package.json')} must name the entry point in \`main\` (a file that default-exports pipeline({...}))`,
    );
  const entry = join(dir, main);
  if (!existsSync(entry))
    throw new ProjectError(
      `${join(dirArg, main)} not found (it must default-export pipeline({...}))`,
    );
  return entry;
}

/** The default export of the entry point. */
export async function loadPipeline(project: Project): Promise<unknown> {
  const mod = (await import(project.pipelineFile)) as { default?: unknown };
  if (!mod.default)
    throw new ProjectError(
      `${project.pipelineFile} has no default export (export default pipeline({...})...)`,
    );
  return mod.default;
}

function listFiles(dir: string): string[] {
  const out: string[] = [];
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) out.push(...listFiles(p));
    else if (e.isFile()) out.push(p);
  }
  return out;
}

let cached: HarnessInfo | undefined;
/**
 * Harness identity: Bun version, SDK version, and a hash over the SDK's runtime sources (src/,
 * without the build and dev tools) plus the zod version the SDK resolves.
 */
export function harnessInfo(): HarnessInfo {
  if (cached) return cached;
  const pkg = JSON.parse(readFileSync(join(SDK_ROOT, 'package.json'), 'utf8')) as {
    version: string;
  };
  const src = join(SDK_ROOT, 'src');
  const files = listFiles(src)
    .map((f) => relative(src, f).split('\\').join('/'))
    .filter(
      (f) =>
        !f.startsWith('build/') &&
        !f.startsWith('dev/') &&
        !f.startsWith('testing/') &&
        f.endsWith('.ts'),
    )
    .sort();
  const h = new Bun.CryptoHasher('sha256');
  for (const f of files) h.update(`${f}\0${sha256Hex(readFileSync(join(src, f)))}\n`);
  let zodVersion = 'unknown';
  try {
    const zodPkg = Bun.resolveSync('zod/package.json', SDK_ROOT);
    zodVersion = (JSON.parse(readFileSync(zodPkg, 'utf8')) as { version: string })
      .version;
  } catch {
    /* keep unknown */
  }
  h.update(`zod@${zodVersion}\n`);
  cached = { bun: Bun.version, sdk: pkg.version, sdkHash: 'sha256:' + h.digest('hex') };
  return cached;
}

export const relPosix = (from: string, to: string) => {
  const r = relative(from, to).split('\\').join('/');
  return r.startsWith('.') ? r : `./${r}`;
};
export { dirname };
