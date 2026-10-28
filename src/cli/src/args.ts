import { UsageError } from './errors';

/** Options every command accepts. `--help` is answered by the runner before a command runs. */
export const globalFlags = new Set(['json', 'verbose', 'help']);
export const globalOptions = new Set(['api-url', 'token', 'profile']);
export const globalAliases: Record<string, string> = { h: 'help', v: 'verbose' };

export type CommandOptions = {
  flags: ReadonlySet<string>;
  options: ReadonlySet<string>;
  aliases: Readonly<Record<string, string>>;
};

/** The parsed command line: `--name value` / `--name=value` options (repeatable), `--flag`s and positionals. */
export class Args {
  operandOffset = 0;

  constructor(
    readonly positionals: readonly string[],
    private readonly values: ReadonlyMap<string, readonly string[]>,
    private readonly flags: ReadonlySet<string>,
  ) {}

  /** The last value given for an option, or none. */
  option(name: string): string | undefined {
    const values = this.values.get(name);
    return values?.[values.length - 1];
  }

  optionOr(name: string, fallback: string): string {
    return this.option(name) ?? fallback;
  }

  /** Every value given for a repeatable option, in order. */
  options(name: string): readonly string[] {
    return this.values.get(name) ?? [];
  }

  flag(name: string): boolean {
    return this.flags.has(name);
  }

  /** The positional after the command words. */
  operand(index: number): string | undefined {
    return this.positionals[this.operandOffset + index];
  }

  get operandCount(): number {
    return Math.max(0, this.positionals.length - this.operandOffset);
  }
}

/** Parses the tokens; a flag with a value or an option without one is a usage error. */
export function parseArgs(args: readonly string[], spec: CommandOptions): Args {
  const positionals: string[] = [];
  const values = new Map<string, string[]>();
  const flags = new Set<string>();

  for (let i = 0; i < args.length; i++) {
    const token = args[i]!;
    if (!token.startsWith('-') || token === '-') {
      positionals.push(token);
      continue;
    }
    let name = token.replace(/^-+/, '');
    let inline: string | undefined;
    const eq = name.indexOf('=');
    if (eq >= 0) {
      inline = name.slice(eq + 1);
      name = name.slice(0, eq);
    }
    name = spec.aliases[name] ?? name;

    if (spec.flags.has(name)) {
      if (inline !== undefined)
        throw new UsageError(`Flag '--${name}' does not take a value.`);
      flags.add(name);
      continue;
    }

    let value: string;
    if (inline !== undefined) value = inline;
    else {
      if (i + 1 >= args.length)
        throw new UsageError(`Option '--${name}' requires a value.`);
      value = args[++i]!;
    }
    const list = values.get(name) ?? [];
    list.push(value);
    values.set(name, list);
  }

  return new Args(positionals, values, flags);
}

/**
 * Global options may lead (`pl --profile beta pipeline list`); they are moved behind the command
 * words so the first positionals are always the noun and verb.
 */
export function globalsLast(args: readonly string[]): string[] {
  const leading: string[] = [];
  let i = 0;
  while (i < args.length) {
    const token = args[i]!;
    if (
      !token.startsWith('-') ||
      token === '--help' ||
      token === '-h' ||
      token === '--version'
    )
      break;
    const raw = token.replace(/^-+/, '').split('=', 1)[0]!;
    const name = globalAliases[raw] ?? raw;
    if (globalFlags.has(name) || token.includes('=')) {
      if (!globalFlags.has(name) && !globalOptions.has(name)) break;
      leading.push(args[i++]!);
    } else if (globalOptions.has(name) && i + 1 < args.length) {
      leading.push(args[i++]!, args[i++]!);
    } else break;
  }
  return leading.length === 0 ? [...args] : [...args.slice(i), ...leading];
}

/**
 * The options the command doesn't know, checked on the raw tokens before parsing: a mistyped
 * option would otherwise swallow the next token as its value.
 */
export function unknownOptions(
  args: readonly string[],
  key: string,
  spec: CommandOptions,
): string[] {
  const problems: string[] = [];
  for (let i = 0; i < args.length; i++) {
    const token = args[i]!;
    if (!token.startsWith('-') || token === '-') continue;
    let name = token.replace(/^-+/, '');
    const inline = name.includes('=');
    if (inline) name = name.slice(0, name.indexOf('='));
    name = spec.aliases[name] ?? name;
    if (spec.flags.has(name)) continue;
    if (spec.options.has(name)) {
      if (!inline) i++;
      continue;
    }
    const hint = closest(name, [...spec.options, ...spec.flags]);
    problems.push(
      hint
        ? `Unknown option '--${name}' for \`pl ${key}\` (did you mean '--${hint}'?). Run \`pl ${key} --help\`.`
        : `Unknown option '--${name}' for \`pl ${key}\`. Run \`pl ${key} --help\`.`,
    );
  }
  return problems;
}

function closest(name: string, candidates: string[]): string | undefined {
  let best: { candidate: string; distance: number } | undefined;
  for (const candidate of candidates) {
    const d = distance(name, candidate);
    if (d <= 2 && (!best || d < best.distance)) best = { candidate, distance: d };
  }
  return best?.candidate;
}

/** Damerau-Levenshtein, for the "did you mean" hint. */
function distance(a: string, b: string): number {
  const d: number[][] = Array.from({ length: a.length + 1 }, (_, i) =>
    Array.from({ length: b.length + 1 }, (_, j) => (i === 0 ? j : j === 0 ? i : 0)),
  );
  for (let i = 1; i <= a.length; i++)
    for (let j = 1; j <= b.length; j++) {
      const cost = a[i - 1] === b[j - 1] ? 0 : 1;
      d[i]![j] = Math.min(d[i - 1]![j]! + 1, d[i]![j - 1]! + 1, d[i - 1]![j - 1]! + cost);
      if (i > 1 && j > 1 && a[i - 1] === b[j - 2] && a[i - 2] === b[j - 1])
        d[i]![j] = Math.min(d[i]![j]!, d[i - 2]![j - 2]! + 1);
    }
  return d[a.length]![b.length]!;
}
