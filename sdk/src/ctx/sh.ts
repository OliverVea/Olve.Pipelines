import { chmodSync, existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { basename, isAbsolute, join } from "node:path";
import type { ExecOptions, Sh, ShOptions } from "./types.ts";
import { PRIVATE_ENV } from "../harness/env.ts";

/** Shells looked for, in order. kaniko only has /busybox/sh; distroless has none. */
export const SHELL_CANDIDATES = ["/bin/sh", "/busybox/sh", "/bin/bash"] as const;

export class ShellNotFoundError extends Error {
  override name = "ShellNotFoundError";
  constructor() {
    super(
      `no shell found in this image (looked for ${SHELL_CANDIDATES.join(", ")}). ` +
        `ctx.sh needs a shell: use an image that has one, or run binaries directly with ctx.exec([...]).`,
    );
  }
}

export class CommandError extends Error {
  override name = "CommandError";
  constructor(readonly what: string, readonly exitCode: number | null, readonly signalCode: string | null) {
    super(`${what} failed with ${signalCode ? `signal ${signalCode}` : `exit code ${exitCode}`}`);
  }
}

export function detectShell(candidates: readonly string[] = SHELL_CANDIDATES): string {
  for (const c of candidates) if (existsSync(c)) return c;
  throw new ShellNotFoundError();
}

/** True for the value of `import x from "./x.sh" with { type: "file" }` (compiled or from source). */
export function isEmbeddedScript(s: string): boolean {
  if (s.startsWith("/$bunfs/") || s.startsWith("B:/~BUN/")) return true;
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
  for (const [k, v] of Object.entries(process.env)) if (v !== undefined && !PRIVATE_ENV.includes(k)) env[k] = v;
  return { ...env, ...extra };
}

export interface ShDeps {
  tmpDir: string;
  signal: AbortSignal;
  shellCandidates?: readonly string[];
}

export function createSh(deps: ShDeps): { sh: Sh; exec: (argv: readonly string[], opts?: ExecOptions) => Promise<void> } {
  const copied = new Map<string, string>();
  let counter = 0;

  // Embedded scripts live in the binary's /$bunfs, which child processes cannot see: copy them out.
  const materialize = (script: string): string => {
    const hit = copied.get(script);
    if (hit) return hit;
    const dir = join(deps.tmpDir, "scripts");
    mkdirSync(dir, { recursive: true });
    const dest = join(dir, `${++counter}-${basename(script)}`);
    writeFileSync(dest, readFileSync(script));
    chmodSync(dest, 0o755);
    copied.set(script, dest);
    return dest;
  };

  const argvFor = (cmd: string | { script: string }, args: readonly string[]): { argv: string[]; what: string } => {
    const scriptPath = typeof cmd === "object" ? cmd.script : isEmbeddedScript(cmd) ? cmd : undefined;
    if (scriptPath === undefined) {
      const shell = detectShell(deps.shellCandidates);
      return { argv: [shell, "-c", cmd as string], what: `command \`${truncate(cmd as string)}\`` };
    }
    const file = materialize(scriptPath);
    const first = readFileSync(file, "utf8").split("\n", 1)[0] ?? "";
    const shebang = /^#!\s*(\S+)(?:\s+(.*))?$/.exec(first);
    if (shebang && existsSync(shebang[1]!)) {
      const interpArgs = shebang[2] ? [shebang[2].trim()] : [];
      return { argv: [shebang[1]!, ...interpArgs, file, ...args], what: `script ${basename(scriptPath)}` };
    }
    return { argv: [detectShell(deps.shellCandidates), file, ...args], what: `script ${basename(scriptPath)}` };
  };

  const run = async (argv: string[], what: string, opts: { env?: Record<string, string>; cwd?: string }, capture: boolean): Promise<string> => {
    if (deps.signal.aborted) throw new Error(`${what} not started: the step was cancelled`);
    let proc: Bun.Subprocess<"ignore", "pipe" | "inherit", "inherit">;
    try {
      proc = Bun.spawn(argv, {
        cwd: opts.cwd,
        env: childEnv(opts.env),
        stdin: "ignore",
        stdout: capture ? "pipe" : "inherit",
        stderr: "inherit",
      });
    } catch (e) {
      throw new Error(`cannot start ${what} (${argv[0]}): ${(e as Error).message}`);
    }
    const onAbort = () => proc.kill("SIGTERM");
    deps.signal.addEventListener("abort", onAbort, { once: true });
    try {
      const text = capture ? await new Response(proc.stdout as ReadableStream).text() : "";
      const code = await proc.exited;
      if (code !== 0) throw new CommandError(what, proc.signalCode ? null : code, proc.signalCode ? String(proc.signalCode) : null);
      return text;
    } finally {
      deps.signal.removeEventListener("abort", onAbort);
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
    if (argv.length === 0) throw new Error("ctx.exec needs at least the program");
    await run([...argv], `\`${truncate(argv.join(" "))}\``, opts, false);
  };
  return { sh, exec };
}

function truncate(s: string, n = 120): string {
  const one = s.replace(/\s+/g, " ").trim();
  return one.length > n ? one.slice(0, n - 1) + "…" : one;
}
