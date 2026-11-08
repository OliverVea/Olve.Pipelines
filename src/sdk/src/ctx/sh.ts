import {
  chmodSync,
  existsSync,
  mkdirSync,
  readFileSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { basename, isAbsolute, join } from 'node:path';
import type { ExecOptions, Sh, ShOptions } from './types.ts';
import { PRIVATE_ENV } from '../harness/env.ts';

/** Shells looked for, in order. kaniko only has /busybox/sh; distroless has none. */
export const SHELL_CANDIDATES = ['/bin/sh', '/busybox/sh', '/bin/bash'] as const;

export class ShellNotFoundError extends Error {
  override name = 'ShellNotFoundError';
  constructor() {
    super(
      `no shell found in this image (looked for ${SHELL_CANDIDATES.join(', ')}). ` +
        `ctx.sh needs a shell: use an image that has one, or run binaries directly with ctx.exec([...]).`,
    );
  }
}

export class CommandError extends Error {
  override name = 'CommandError';
  constructor(
    readonly what: string,
    readonly exitCode: number | null,
    readonly signalCode: string | null,
  ) {
    super(
      `${what} failed with ${signalCode ? `signal ${signalCode}` : `exit code ${exitCode}`}`,
    );
  }
}

/** The command went quiet for longer than its `idleTimeoutSeconds` and was killed. */
export class CommandStalledError extends CommandError {
  override name = 'CommandStalledError';
  constructor(
    what: string,
    readonly idleTimeoutSeconds: number,
    signalCode: string | null,
  ) {
    super(what, null, signalCode);
    this.message = `${what} produced no output for ${idleTimeoutSeconds} s and was killed`;
  }
}

/** Grace between the watchdog's SIGTERM and SIGKILL (a process blocked in a socket read may ignore TERM). */
export const STALL_KILL_GRACE_MS = 10_000;

export function detectShell(candidates: readonly string[] = SHELL_CANDIDATES): string {
  for (const c of candidates) if (existsSync(c)) return c;
  throw new ShellNotFoundError();
}

/** True for the value of `import x from "./x.sh" with { type: "file" }` (compiled or from source). */
export function isEmbeddedScript(s: string): boolean {
  if (s.startsWith('/$bunfs/') || s.startsWith('B:/~BUN/')) return true;
  if (!isAbsolute(s) || !/\.(sh|bash)$/.test(s) || /\s/.test(s)) return false;
  try {
    return statSync(s).isFile();
  } catch {
    return false;
  }
}

/** Environment for child processes: the harness env minus harness-only credentials, plus extras. */
export function childEnv(extra?: Record<string, string>): Record<string, string> {
  const env: Record<string, string> = {};
  for (const [k, v] of Object.entries(process.env))
    if (v !== undefined && !PRIVATE_ENV.includes(k)) env[k] = v;
  return { ...env, ...extra };
}

export interface ShDeps {
  tmpDir: string;
  signal: AbortSignal;
  shellCandidates?: readonly string[];
}

export function createSh(deps: ShDeps): {
  sh: Sh;
  exec: (argv: readonly string[], opts?: ExecOptions) => Promise<void>;
} {
  const copied = new Map<string, string>();
  let counter = 0;

  // Embedded scripts live in the binary's /$bunfs, which child processes cannot see: copy them out.
  const materialize = (script: string): string => {
    const hit = copied.get(script);
    if (hit) return hit;
    const dir = join(deps.tmpDir, 'scripts');
    mkdirSync(dir, { recursive: true });
    const dest = join(dir, `${++counter}-${basename(script)}`);
    writeFileSync(dest, readFileSync(script));
    chmodSync(dest, 0o755);
    copied.set(script, dest);
    return dest;
  };

  const argvFor = (
    cmd: string | { script: string },
    args: readonly string[],
  ): { argv: string[]; what: string } => {
    const scriptPath =
      typeof cmd === 'object' ? cmd.script : isEmbeddedScript(cmd) ? cmd : undefined;
    if (scriptPath === undefined) {
      const shell = detectShell(deps.shellCandidates);
      // `sh -c cmd sh a b`: the args are $1, $2… of the command.
      return {
        argv: [shell, '-c', cmd as string, 'sh', ...args],
        what: `command \`${truncate(cmd as string)}\``,
      };
    }
    const file = materialize(scriptPath);
    const first = readFileSync(file, 'utf8').split('\n', 1)[0] ?? '';
    const shebang = /^#!\s*(\S+)(?:\s+(.*))?$/.exec(first);
    if (shebang && existsSync(shebang[1]!)) {
      const interpArgs = shebang[2] ? [shebang[2].trim()] : [];
      return {
        argv: [shebang[1]!, ...interpArgs, file, ...args],
        what: `script ${basename(scriptPath)}`,
      };
    }
    return {
      argv: [detectShell(deps.shellCandidates), file, ...args],
      what: `script ${basename(scriptPath)}`,
    };
  };

  const run = async (
    argv: string[],
    what: string,
    opts: { env?: Record<string, string>; cwd?: string; idleTimeoutSeconds?: number },
    capture: boolean,
  ): Promise<string> => {
    if (deps.signal.aborted)
      throw new Error(`${what} not started: the step was cancelled`);
    const idleSeconds =
      opts.idleTimeoutSeconds !== undefined && opts.idleTimeoutSeconds > 0
        ? opts.idleTimeoutSeconds
        : undefined;
    let proc: Bun.Subprocess<'ignore', 'pipe' | 'inherit', 'pipe' | 'inherit'>;
    try {
      proc = Bun.spawn(argv, {
        cwd: opts.cwd,
        env: childEnv(opts.env),
        stdin: 'ignore',
        stdout: capture || idleSeconds ? 'pipe' : 'inherit',
        stderr: idleSeconds ? 'pipe' : 'inherit',
      });
    } catch (e) {
      throw new Error(`cannot start ${what} (${argv[0]}): ${(e as Error).message}`);
    }
    const onAbort = () => proc.kill('SIGTERM');
    deps.signal.addEventListener('abort', onAbort, { once: true });

    // Watchdog: every chunk of output re-arms the timer; on expiry TERM, then KILL after a grace.
    let stalled = false;
    let idleTimer: ReturnType<typeof setTimeout> | undefined;
    let killTimer: ReturnType<typeof setTimeout> | undefined;
    const arm = () => {
      if (!idleSeconds || stalled) return;
      clearTimeout(idleTimer);
      idleTimer = setTimeout(() => {
        stalled = true;
        proc.kill('SIGTERM');
        killTimer = setTimeout(() => proc.kill('SIGKILL'), STALL_KILL_GRACE_MS);
      }, idleSeconds * 1000);
    };
    const captured: Uint8Array[] = [];
    const readers: { cancel(): Promise<void> }[] = [];
    const pump = async (
      stream: ReadableStream<Uint8Array>,
      sink: (chunk: Uint8Array) => void,
    ) => {
      const reader = stream.getReader();
      readers.push(reader);
      try {
        for (;;) {
          const { done, value } = await reader.read();
          if (done) return;
          arm();
          sink(value);
        }
      } catch {
        // cancelled after a stall
      }
    };
    const pumps: Promise<void>[] = [];
    if (capture)
      pumps.push(
        pump(proc.stdout as ReadableStream<Uint8Array>, (c) => captured.push(c)),
      );
    else if (idleSeconds)
      pumps.push(
        pump(proc.stdout as ReadableStream<Uint8Array>, (c) => process.stdout.write(c)),
      );
    if (idleSeconds)
      pumps.push(
        pump(proc.stderr as ReadableStream<Uint8Array>, (c) => process.stderr.write(c)),
      );
    arm();

    try {
      const code = await proc.exited;
      // A killed command's children may still hold the pipes open: don't wait for them after a stall.
      if (stalled) await Promise.all(readers.map((r) => r.cancel().catch(() => {})));
      else await Promise.all(pumps);
      if (stalled)
        throw new CommandStalledError(
          what,
          idleSeconds!,
          proc.signalCode ? String(proc.signalCode) : null,
        );
      if (code !== 0)
        throw new CommandError(
          what,
          proc.signalCode ? null : code,
          proc.signalCode ? String(proc.signalCode) : null,
        );
      return capture ? Buffer.concat(captured).toString('utf8') : '';
    } finally {
      clearTimeout(idleTimer);
      clearTimeout(killTimer);
      deps.signal.removeEventListener('abort', onAbort);
    }
  };

  const sh = Object.assign(
    async (cmd: string | { script: string }, opts: ShOptions = {}) => {
      const { argv, what } = argvFor(cmd, opts.args ?? []);
      await run(argv, what, opts, false);
    },
    {
      text: async (cmd: string | { script: string }, opts: ShOptions = {}) => {
        const { argv, what } = argvFor(cmd, opts.args ?? []);
        return (await run(argv, what, opts, true)).trim();
      },
    },
  ) as Sh;

  const exec = async (argv: readonly string[], opts: ExecOptions = {}) => {
    if (argv.length === 0) throw new Error('ctx.exec needs at least the program');
    await run([...argv], `\`${truncate(argv.join(' '))}\``, opts, false);
  };
  return { sh, exec };
}

function truncate(s: string, n = 120): string {
  const one = s.replace(/\s+/g, ' ').trim();
  return one.length > n ? one.slice(0, n - 1) + '…' : one;
}
