import { type Command, commandKey } from './registry';

const signature = (command: Command): string => commandKey(command).padEnd(28);

export function usage(commands: readonly Command[]): string {
  return [
    'pl — Olve.Pipelines operator + API CLI',
    '',
    'Usage:',
    '  pl <command> [args] [options]',
    '',
    'Commands:',
    ...commands.map((c) => `  ${signature(c)} ${c.summary}`),
    '',
    'Global options (any command):',
    '  --json        Machine-readable JSON output on stdout',
    '  --verbose,-v  Diagnostic logging to stderr',
    "  --profile <n> Use profile <n> of ~/.pl (or PIPELINES_PROFILE; default 'default')",
    '  --help,-h     Show help for the command',
    '  --version     Print the CLI version (alone: `pl --version`)',
    '',
    'Run `pl <command> --help` for command-specific options.',
  ].join('\n');
}

export function nounHelp(commands: readonly Command[], noun: string): string {
  return [
    `pl ${noun} — commands:`,
    '',
    ...commands
      .filter((c) => c.noun === noun)
      .map((c) => `  ${signature(c)} ${c.summary}`),
  ].join('\n');
}

export function commandHelp(command: Command): string {
  return command.detail ?? `${commandKey(command)}   ${command.summary}`;
}
