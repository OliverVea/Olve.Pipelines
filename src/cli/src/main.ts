#!/usr/bin/env bun
import { spawn } from 'node:child_process';
import { run } from './cli';

/** Best effort: the login URL is always printed too. */
function openBrowser(url: string): void {
  const [command, ...args] =
    process.platform === 'darwin'
      ? ['open', url]
      : process.platform === 'win32'
        ? ['cmd', '/c', 'start', '', url]
        : ['xdg-open', url];
  try {
    spawn(command!, args, { stdio: 'ignore', detached: true })
      .on('error', () => {})
      .unref();
  } catch {
    // No opener: the user follows the printed URL.
  }
}

/** A line from the terminal; `hidden` reads it without echo (secret values). */
function prompt(text: string, options?: { hidden?: boolean }): Promise<string> {
  process.stderr.write(text);
  return new Promise((resolve) => {
    const stdin = process.stdin;
    const hidden = options?.hidden === true && stdin.isTTY;
    if (hidden) stdin.setRawMode(true);
    stdin.resume();
    stdin.setEncoding('utf8');
    let value = '';
    const onData = (chunk: string) => {
      for (const ch of chunk) {
        if (ch === '\u0003') process.exit(130);
        if (ch === '\r' || ch === '\n') {
          stdin.removeListener('data', onData);
          if (hidden) stdin.setRawMode(false);
          stdin.pause();
          process.stderr.write('\n');
          return resolve(value);
        }
        if (ch === '\u007f' || ch === '\b') value = value.slice(0, -1);
        else if (ch >= ' ') value += ch;
      }
    };
    stdin.on('data', onData);
  });
}

// Ctrl+C aborts the running command (streams and waits end cleanly, exit 130); a second one forces it.
const interrupt = new AbortController();
process.on('SIGINT', () => {
  if (interrupt.signal.aborted) process.exit(130);
  interrupt.abort();
});

process.exitCode = await run(process.argv.slice(2), {
  stdout: (text) => process.stdout.write(text),
  stderr: (line) => process.stderr.write(`${line}\n`),
  env: process.env,
  signal: interrupt.signal,
  openBrowser,
  interactive: process.stderr.isTTY === true,
  stdoutBytes: (bytes) => process.stdout.write(bytes),
  stdinIsTerminal: process.stdin.isTTY === true,
  prompt,
});
