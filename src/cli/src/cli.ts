import {
  Args,
  globalAliases,
  globalFlags,
  globalOptions,
  globalsLast,
  parseArgs,
  unknownOptions,
} from './args';
import { createApi } from './api';
import { commands as defaultCommands } from './commands';
import { CliError, ExitCode, UsageError, describe } from './errors';
import { commandHelp, nounHelp, usage } from './help';
import { Output } from './output';
import {
  configPath,
  loadProfile,
  refreshIfExpiring,
  resolveApiUrl,
  resolveProfileName,
  resolveToken,
  type Env,
} from './profiles';
import { type Command, type CommandContext, commandKey } from './registry';

/** The version stamp the deploy compiled in (`--define process.env.PL_VERSION`). */
export const VERSION: string = process.env.PL_VERSION || 'dev';

export type Io = {
  stdout(text: string): void;
  stderr(line: string): void;
  env: Env;
  fetch?: typeof fetch;
  /** Aborted on Ctrl+C. */
  signal?: AbortSignal;
  interactive?: boolean;
  openBrowser?(url: string): void;
  now?(): Date;
  sleep?(ms: number): Promise<void>;
  stdin?(): Promise<string>;
  stdoutBytes?(bytes: Uint8Array): void;
  stdinIsTerminal?: boolean;
  prompt?(text: string, options?: { hidden?: boolean }): Promise<string>;
};

/** Runs one invocation and returns the exit code. Never calls process.exit. */
export async function run(
  argv: readonly string[],
  io: Io,
  commands: readonly Command[] = defaultCommands,
): Promise<number> {
  const args = globalsLast(argv);
  const fail = (problems: readonly string[], code: number = ExitCode.Failure): number => {
    for (const problem of problems) io.stderr(`error: ${problem}`);
    return code;
  };

  if (args.length === 0) {
    io.stdout(usage(commands) + '\n');
    return ExitCode.Usage;
  }
  if (args.length === 1 && args[0] === '--version') {
    io.stdout(`pl ${VERSION}\n`);
    return ExitCode.Ok;
  }

  const helpRequested = args.some((a) => a === '--help' || a === '-h');
  const peek0 = args[0]!.startsWith('-') ? undefined : args[0];
  const peek1 = args.length > 1 && !args[1]!.startsWith('-') ? args[1] : undefined;
  const hasNoun = (noun: string) => commands.some((c) => c.noun === noun);

  if (peek0 === undefined) {
    io.stdout(usage(commands) + '\n');
    return helpRequested ? ExitCode.Ok : ExitCode.Usage;
  }
  if (peek0 === 'help') {
    io.stdout(
      (peek1 && hasNoun(peek1) ? nounHelp(commands, peek1) : usage(commands)) + '\n',
    );
    return ExitCode.Ok;
  }

  let command: Command | undefined;
  let offset = 0;
  if (peek1 !== undefined) {
    command = commands.find((c) => c.noun === peek0 && c.verb === peek1);
    if (command) offset = 2;
  }
  if (!command) {
    command = commands.find((c) => c.noun === peek0 && !c.verb);
    if (command) offset = 1;
  }
  if (!command) {
    if (hasNoun(peek0)) {
      if (helpRequested || peek1 === undefined) {
        io.stdout(nounHelp(commands, peek0) + '\n');
        return helpRequested ? ExitCode.Ok : ExitCode.Usage;
      }
      return fail(
        [`Unknown command '${peek0} ${peek1}'. Run \`pl ${peek0} --help\`.`],
        ExitCode.Usage,
      );
    }
    return fail([`Unknown command '${peek0}'. Run \`pl help\`.`], ExitCode.Usage);
  }
  if (helpRequested) {
    io.stdout(commandHelp(command) + '\n');
    return ExitCode.Ok;
  }

  const key = commandKey(command);
  const spec = {
    flags: new Set([...globalFlags, ...(command.flags ?? [])]),
    options: new Set([...globalOptions, ...(command.options ?? [])]),
    aliases: { ...globalAliases, ...(command.aliases ?? {}) },
  };
  const unknown = unknownOptions(args, key, spec);
  if (unknown.length) return fail(unknown, ExitCode.Usage);

  let parsed: Args;
  try {
    parsed = parseArgs(args, spec);
  } catch (error) {
    return fail([describe(error)], ExitCode.Usage);
  }
  parsed.operandOffset = offset;
  const required = command.operands ?? 0;
  if (parsed.operandCount < required)
    return fail(
      [`\`pl ${key}\` expects ${required} argument(s). Run \`pl ${key} --help\`.`],
      ExitCode.Usage,
    );

  const verbose = parsed.flag('verbose');
  const log = (message: string) => {
    if (verbose) io.stderr(message);
  };
  const fetchFn = io.fetch ?? fetch;
  const now = io.now ?? (() => new Date());
  const path = configPath(io.env);
  const signal = io.signal ?? new AbortController().signal;

  let ctx: CommandContext;
  try {
    const profile = loadProfile(path, resolveProfileName(parsed, io.env));
    const { url: apiUrl, source: urlSource } = resolveApiUrl(parsed, io.env, profile);
    const resolved = resolveToken(parsed, io.env, profile, apiUrl);
    try {
      new URL(apiUrl);
    } catch {
      return fail([`Invalid API URL '${apiUrl}'.`]);
    }
    log(
      `api: ${apiUrl} (from ${urlSource}); token: ${resolved.token ? resolved.source : 'none'}`,
    );
    const usesApi = command.usesApi ?? true;
    if (usesApi && resolved.owned)
      await refreshIfExpiring(path, profile, { fetch: fetchFn, log, now });

    const token = async () => {
      if (!resolved.owned) return resolved.token;
      await refreshIfExpiring(path, profile, { fetch: fetchFn, log, now });
      return resolveToken(parsed, io.env, profile, apiUrl).token;
    };
    const api = createApi({ baseUrl: apiUrl, fetch: fetchFn, log, token });
    ctx = {
      args: parsed,
      json: parsed.flag('json'),
      verbose,
      out: new Output(parsed.flag('json'), io.stdout),
      log,
      stderr: io.stderr,
      api,
      token,
      apiUrl,
      profile,
      configPath: path,
      env: io.env,
      fetch: fetchFn,
      signal,
      now,
      sleep: io.sleep ?? ((ms) => new Promise((resolve) => setTimeout(resolve, ms))),
      interactive: io.interactive ?? false,
      headless: isHeadless(io.env),
      openBrowser: io.openBrowser ?? (() => {}),
      stdin: io.stdin ?? (() => Bun.stdin.text()),
      stdoutBytes:
        io.stdoutBytes ?? ((bytes) => io.stdout(new TextDecoder().decode(bytes))),
      stdinIsTerminal: io.stdinIsTerminal ?? false,
      prompt: io.prompt ?? (async () => ''),
    };
  } catch (error) {
    if (error instanceof CliError) return fail(error.problems);
    throw error;
  }

  try {
    await command.run(ctx);
    return ctx.exitCode ?? ExitCode.Ok;
  } catch (error) {
    if (signal.aborted) return ExitCode.Cancelled;
    if (error instanceof UsageError) return fail([error.message], ExitCode.Usage);
    if (error instanceof CliError) return fail(error.problems);
    if (verbose)
      io.stderr(error instanceof Error && error.stack ? error.stack : String(error));
    return fail([`${describe(error)} (--verbose for details)`]);
  }
}

/** No browser a loopback redirect could reach: over SSH, or (Linux) without a display. */
function isHeadless(env: Env): boolean {
  if (env.SSH_CONNECTION || env.SSH_TTY) return true;
  return process.platform === 'linux' && !env.DISPLAY && !env.WAYLAND_DISPLAY;
}
