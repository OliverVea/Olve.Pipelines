/**
 * Secret masking for `run`: when the service names the step's secrets (`OLVE_SECRETS`), the binary
 * runs itself as a child and masks the child's stdout and stderr bytes, so every way of printing is
 * covered, from the pipeline's first import to its last timer. It runs before the pipeline module
 * is imported.
 */
import { readFileSync } from 'node:fs';
import { constants } from 'node:os';
import { lineMasker, maskPatterns } from './mask.ts';

const SUPERVISED = 'OLVE_SUPERVISED';
const FORWARDED = ['SIGTERM', 'SIGINT', 'SIGHUP'] as const;

/** The secret values this run must not print, from `OLVE_SECRETS` (names, comma-separated). */
export function secretPatterns(env: NodeJS.ProcessEnv = process.env): string[] {
  const names = (env.OLVE_SECRETS ?? '').split(',').filter(Boolean);
  return maskPatterns(names.map((n) => env[n]));
}

/** This process's own command line, loader included (`ld-linux … /olve/step run x` in a pod). */
function ownArgv(): string[] {
  try {
    const argv = readFileSync('/proc/self/cmdline', 'utf8').split('\0').filter(Boolean);
    if (argv.length > 0) return argv;
  } catch {
    // not Linux
  }
  return [process.execPath, ...process.argv.slice(1)];
}

/**
 * Runs the step as a masked child and answers its exit code, or undefined when this process should
 * run the step itself: it is the child, the mode isn't `run`, or there is nothing to mask.
 */
export async function supervise(argv: readonly string[]): Promise<number | undefined> {
  if (argv[0] !== 'run' || process.env[SUPERVISED]) return undefined;
  const patterns = secretPatterns();
  if (patterns.length === 0) return undefined;

  const child = Bun.spawn(ownArgv(), {
    env: { ...process.env, [SUPERVISED]: '1' },
    stdin: 'inherit',
    stdout: 'pipe',
    stderr: 'pipe',
  });
  const forward = (signal: NodeJS.Signals) => () => child.kill(signal);
  for (const s of FORWARDED) process.on(s, forward(s));

  const pump = async (stream: ReadableStream<Uint8Array>, fd: 1 | 2) => {
    const out = fd === 1 ? process.stdout : process.stderr;
    const masker = lineMasker(patterns, (text) => void out.write(text));
    for await (const chunk of stream) masker.write(chunk);
    masker.flush();
  };
  const pumps = Promise.all([pump(child.stdout, 1), pump(child.stderr, 2)]);
  await child.exited;
  // A detached grandchild may hold the pipes open: don't wait for it long.
  await Promise.race([pumps, Bun.sleep(5000)]);
  const signal = child.signalCode
    ? constants.signals[child.signalCode as NodeJS.Signals]
    : undefined;
  return child.exitCode ?? (signal ? 128 + signal : 1);
}
