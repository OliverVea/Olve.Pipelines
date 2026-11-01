/** Exit codes, kept from the C# `pl`: scripts and the agent playbook depend on them. */
export const ExitCode = {
  Ok: 0,
  /** A failed command: the server refused, the network failed, a wait ended in failure. */
  Failure: 1,
  Usage: 2,
  /** `pl wait`: the run waits for a person's input. */
  NeedsInput: 3,
  /** `pl wait`: the run is held at a blocked step. */
  Held: 4,
  /** `pl wait --timeout` elapsed. */
  Timeout: 124,
  /** Ctrl+C. */
  Cancelled: 130,
} as const;

/** A command failed: one `error:` line per problem on stderr, exit 1. */
export class CliError extends Error {
  readonly problems: string[];

  constructor(
    problems: string | string[],
    /** The server or the network may answer differently in a moment (5xx, 429, no connection). */
    readonly retryable = false,
  ) {
    const list = Array.isArray(problems) ? problems : [problems];
    super(list.join('; '));
    this.name = 'CliError';
    this.problems = list;
  }
}

/** A bad invocation: unknown command or option, missing arguments, a malformed value. Exit 2. */
export class UsageError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'UsageError';
  }
}

/** The message of any thrown value. */
export function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
