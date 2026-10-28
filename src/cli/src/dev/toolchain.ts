import { existsSync, readFileSync } from 'node:fs';
import { delimiter, dirname, join, resolve } from 'node:path';
import { CliError } from '../errors';
import { diagnostic, succeeded, type ProcessRunner } from '../process';

export const BUN_ENV = 'OLVE_BUN';
export const SDK_ENV = 'OLVE_SDK';

/** Bun and the SDK's `olve-dev`/`olve-build`: the SDK the pipeline's package.json pins, as installed beside it. */
export type Toolchain = { bun: string; devCli: string; buildCli: string };

export function findBun(env: Record<string, string | undefined>): string {
  const fromEnv = env[BUN_ENV];
  if (fromEnv) {
    if (existsSync(fromEnv)) return fromEnv;
    throw new CliError(`$${BUN_ENV} is set to '${fromEnv}', which does not exist.`);
  }
  const exe = process.platform === 'win32' ? 'bun.exe' : 'bun';
  for (const dir of (env.PATH ?? '').split(delimiter))
    if (dir && existsSync(join(dir, exe))) return join(dir, exe);
  const fallback = join(env.HOME || env.USERPROFILE || '', '.bun', 'bin', exe);
  if (existsSync(fallback)) return fallback;
  throw new CliError(
    `Bun not found: \`pl dev\` runs pipelines with Bun. Install it (https://bun.sh), put it on PATH, or set $${BUN_ENV} to the bun binary.`,
  );
}

export async function resolveToolchain(
  run: ProcessRunner,
  pipelineDir: string,
  env: Record<string, string | undefined>,
): Promise<Toolchain> {
  const bun = findBun(env);
  let sdkRoot: string;
  if (env[SDK_ENV]) sdkRoot = resolve(env[SDK_ENV]!);
  else {
    const probe = await run({
      command: bun,
      args: [
        '-e',
        "process.stdout.write(Bun.resolveSync('@olivervea/pipelines', process.argv[1]))",
        pipelineDir.replace(/\/+$/, '') + '/',
      ],
    });
    if (!succeeded(probe) || !probe.stdout.trim())
      throw new CliError(
        `Cannot resolve @olivervea/pipelines from '${pipelineDir}' (run \`bun install\` there, or set $${SDK_ENV} to the SDK directory): ${diagnostic(probe)}`,
      );
    const root = packageRoot(probe.stdout.trim());
    if (!root) throw new CliError(`No package.json above '${probe.stdout.trim()}'.`);
    sdkRoot = root;
  }
  const devCli = binPath(sdkRoot, 'olve-dev', 'src/dev/cli.ts');
  const buildCli = binPath(sdkRoot, 'olve-build', 'src/build/cli.ts');
  if (!existsSync(devCli))
    throw new CliError(`The SDK at '${sdkRoot}' has no olve-dev (${devCli} missing).`);
  return { bun, devCli, buildCli };
}

function packageRoot(path: string): string | undefined {
  let dir = dirname(path);
  for (;;) {
    if (existsSync(join(dir, 'package.json'))) return dir;
    const parent = dirname(dir);
    if (parent === dir) return undefined;
    dir = parent;
  }
}

function binPath(sdkRoot: string, bin: string, fallback: string): string {
  try {
    const pkg = JSON.parse(readFileSync(join(sdkRoot, 'package.json'), 'utf8')) as {
      bin?: Record<string, string>;
    };
    if (typeof pkg.bin?.[bin] === 'string') return resolve(sdkRoot, pkg.bin[bin]);
  } catch {
    // the known layout below
  }
  return resolve(sdkRoot, fallback);
}
