import type { Client } from '@olve/api/client';
import type { Args } from './args';
import type { Output } from './output';
import type { Env, Profile } from './profiles';

export type CommandContext = {
  args: Args;
  json: boolean;
  verbose: boolean;
  out: Output;
  /** Diagnostics on stderr, only with `--verbose`. */
  log(message: string): void;
  /** Warnings and progress on stderr, always. */
  stderr(line: string): void;
  api: Client;
  /** The bearer for a raw request outside the contract (event streams, file downloads). */
  token(): Promise<string | undefined>;
  apiUrl: string;
  profile: Profile;
  /** Where the profile file lives. */
  configPath: string;
  env: Env;
  fetch: typeof fetch;
  /** Aborted on Ctrl+C. */
  signal: AbortSignal;
  now(): Date;
  sleep(ms: number): Promise<void>;
  /** stderr is a terminal (QR codes and progress belong there only then). */
  interactive: boolean;
  /** No browser a loopback redirect could reach: over SSH, or without a display. */
  headless: boolean;
  openBrowser(url: string): void;
  /** The whole of stdin, for `@-` inputs. */
  stdin(): Promise<string>;
  /** Raw bytes to stdout (file downloads). */
  stdoutBytes(bytes: Uint8Array): void;
  /** stdin is a terminal: nothing is piped, so a value must be prompted for or given otherwise. */
  stdinIsTerminal: boolean;
  /** Asks on the terminal; `hidden` for secrets (no echo). */
  prompt(text: string, options?: { hidden?: boolean }): Promise<string>;
  /** A command's own exit code (waits and checks); 0 when unset. */
  exitCode?: number;
};

export type Command = {
  noun: string;
  /** None for a verbless command (`pl trigger`, `pl login`). */
  verb?: string;
  summary: string;
  /** `pl <noun> <verb> --help`; the summary alone otherwise. */
  detail?: string;
  flags?: readonly string[];
  options?: readonly string[];
  aliases?: Readonly<Record<string, string>>;
  /** Required positionals after the command words. */
  operands?: number;
  /** Builds the API client and refreshes the login first (default true). */
  usesApi?: boolean;
  run(ctx: CommandContext): Promise<void>;
};

export const commandKey = (command: Pick<Command, 'noun' | 'verb'>): string =>
  command.verb ? `${command.noun} ${command.verb}` : command.noun;
