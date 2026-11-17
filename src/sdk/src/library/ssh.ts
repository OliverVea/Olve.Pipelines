// sshSession — run commands on a host over SSH from a handler (installs openssh-client when the image lacks ssh).
import { chmodSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Ctx } from '../ctx/types.ts';

const SAFE = /^[A-Za-z0-9_\/.:=@%+,-]+$/;
/** POSIX shell quoting (safe words are left bare, so logs stay readable). */
export function shq(s: string): string {
  return SAFE.test(s) ? s : `'${s.replace(/'/g, `'\\''`)}'`;
}

/** Installs the ssh client unless the image has one: apk (Alpine) or apt-get (Debian, Ubuntu). */
export const SSH_CLIENT_INSTALL =
  'command -v ssh >/dev/null 2>&1 || if command -v apk >/dev/null 2>&1; then apk add --no-cache openssh-client; ' +
  'else apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends openssh-client; fi';

export interface SshSession {
  readonly host: string;
  /**
   * Runs a remote command; a non-zero exit throws. With `tty`, the command gets a terminal, so a client that
   * dies (a cancelled step) hangs it up: without one, the command gets no signal and carries on.
   */
  run(remote: string, options?: { tty?: boolean }): Promise<void>;
  /** Runs a remote command and returns its stdout. */
  text(remote: string): Promise<string>;
  /** `scp -r` a local file or directory to a remote path. */
  copy(local: string, remote: string): Promise<void>;
  /** The ssh invocation as a shell command, for pipes and redirects: `ctx.sh(`${s.shell("cat > f")} < ${file}`)`. */
  shell(remote: string): string;
}

/**
 * The host's public key, as `ssh-keyscan <host>` prints it (`<host> ssh-ed25519 AAAA…`, one line per
 * key type; the host name column may be left out), or `false` to connect without verifying the host.
 */
export type HostKey = string | false;

const KEY_TYPE = /^(ssh-[a-z0-9-]+|ecdsa-sha2-[a-z0-9-]+|sk-[a-z0-9@.-]+)$/;
const KEY_DATA = /^[A-Za-z0-9+/]+={0,3}$/;
// Every key is written under this name and ssh looks the host up by it, so `user@name`, an IP or a
// port in `host` can't make a correct key miss.
const HOST_KEY_ALIAS = 'olve-ssh-host';

/** The `known_hosts` content for `hostKey`, or an error naming what's wrong with it. */
export function knownHosts(hostKey: string): string {
  const lines = hostKey
    .split(/\r?\n/)
    .map((l) => l.trim())
    .filter((l) => l && !l.startsWith('#'));
  const keys = lines.map((line) => {
    const words = line.split(/\s+/);
    const at = words.findIndex((w) => KEY_TYPE.test(w));
    if (at < 0 || at > 1 || !KEY_DATA.test(words[at + 1] ?? ''))
      throw new Error(
        `hostKey: '${line}' is not a public key; give it as ssh-keyscan prints it, e.g. 'ssh-ed25519 AAAA…'`,
      );
    return `${HOST_KEY_ALIAS} ${words[at]} ${words[at + 1]}`;
  });
  if (!keys.length)
    throw new Error(
      `hostKey is empty: give the host's public key (ssh-keyscan <host>), or false to skip verifying the host`,
    );
  return keys.join('\n') + '\n';
}

/**
 * Opens an SSH "session" to `host` (`user@host`) with a private key: installs openssh-client if needed,
 * writes the key to `<tmpDir>/ssh/id`. The host must present a key in `hostKey`, unless it is `false`.
 */
export async function sshSession(
  ctx: Pick<Ctx, 'exec' | 'sh' | 'tmpDir'>,
  opts: { host: string; key: string; hostKey: HostKey },
): Promise<SshSession> {
  if (opts.hostKey === undefined || opts.hostKey === null)
    throw new Error(
      `hostKey for ${opts.host} is not set: give the host's public key (ssh-keyscan <host>), or false to skip verifying the host`,
    );
  const known = opts.hostKey === false ? undefined : knownHosts(opts.hostKey);
  await ctx.sh(SSH_CLIENT_INSTALL);
  const dir = join(ctx.tmpDir, 'ssh');
  mkdirSync(dir, { recursive: true, mode: 0o700 });
  const keyFile = join(dir, 'id');
  // OpenSSH rejects a key without a trailing newline.
  writeFileSync(keyFile, opts.key.endsWith('\n') ? opts.key : opts.key + '\n', {
    mode: 0o600,
  });
  chmodSync(keyFile, 0o600);
  const knownFile = join(dir, 'known_hosts');
  if (known) writeFileSync(knownFile, known);
  const hostOpts = known
    ? [
        'StrictHostKeyChecking=yes',
        `UserKnownHostsFile=${knownFile}`,
        'GlobalKnownHostsFile=/dev/null',
        `HostKeyAlias=${HOST_KEY_ALIAS}`,
      ]
    : ['StrictHostKeyChecking=no', 'UserKnownHostsFile=/dev/null'];
  const sshOpts = [
    '-i',
    keyFile,
    ...hostOpts.flatMap((o) => ['-o', o]),
    '-o',
    'LogLevel=ERROR',
    '-o',
    'BatchMode=yes',
  ];
  const argv = (remote: string) => ['ssh', ...sshOpts, opts.host, remote];
  return {
    host: opts.host,
    run: (remote, options) =>
      ctx.exec(options?.tty ? ['ssh', '-tt', ...argv(remote).slice(1)] : argv(remote)),
    text: (remote) => ctx.sh.text(argv(remote).map(shq).join(' ')),
    copy: (local, remote) =>
      ctx.exec(['scp', ...sshOpts, '-r', local, `${opts.host}:${remote}`]),
    shell: (remote) => argv(remote).map(shq).join(' '),
  };
}
