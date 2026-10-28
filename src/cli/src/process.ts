import { CliError, describe } from './errors';

export type ProcessSpec = {
  command: string;
  args: readonly string[];
  /** Set or, with `null`, remove variables of the inherited environment. */
  env?: Record<string, string | null | undefined>;
  cwd?: string;
  /** Called per line as the child writes; the full text is still returned. */
  onStdoutLine?(line: string): void;
  onStderrLine?(line: string): void;
};

export type ProcessResult = { exitCode: number; stdout: string; stderr: string };

export const succeeded = (r: ProcessResult) => r.exitCode === 0;

/** What a failed child said: stderr, else stdout. */
export const diagnostic = (r: ProcessResult) =>
  r.stderr.trim() ? r.stderr.trim() : r.stdout.trim();

/** Runs child processes; tests substitute one that records and answers. */
export type ProcessRunner = (
  spec: ProcessSpec,
  signal?: AbortSignal,
) => Promise<ProcessResult>;

export const runProcess: ProcessRunner = async (spec, signal) => {
  const env: Record<string, string> = {};
  for (const [k, v] of Object.entries(process.env)) if (v !== undefined) env[k] = v;
  for (const [k, v] of Object.entries(spec.env ?? {})) {
    if (v === null || v === undefined) delete env[k];
    else env[k] = v;
  }
  let child: ReturnType<typeof Bun.spawn>;
  try {
    child = Bun.spawn([spec.command, ...spec.args], {
      env,
      cwd: spec.cwd,
      stdout: 'pipe',
      stderr: 'pipe',
      stdin: 'ignore',
      signal,
    });
  } catch (error) {
    throw new CliError(`Could not start '${spec.command}': ${describe(error)}`);
  }
  const [stdout, stderr, exitCode] = await Promise.all([
    collect(child.stdout as ReadableStream<Uint8Array>, spec.onStdoutLine),
    collect(child.stderr as ReadableStream<Uint8Array>, spec.onStderrLine),
    child.exited,
  ]);
  return { exitCode, stdout, stderr };
};

async function collect(
  stream: ReadableStream<Uint8Array>,
  onLine?: (line: string) => void,
): Promise<string> {
  let text = '';
  let pending = '';
  const decoder = new TextDecoder();
  for await (const bytes of stream) {
    const chunk = decoder.decode(bytes, { stream: true });
    text += chunk;
    if (!onLine) continue;
    pending += chunk;
    let end: number;
    while ((end = pending.indexOf('\n')) >= 0) {
      onLine(pending.slice(0, end).replace(/\r$/, ''));
      pending = pending.slice(end + 1);
    }
  }
  if (onLine && pending) onLine(pending);
  return text;
}
