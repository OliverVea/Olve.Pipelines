// sshSession — run commands on a host over SSH from a handler (alpine image: installs openssh-client).
import { chmodSync, mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Ctx } from "../ctx/types.ts";

const SAFE = /^[A-Za-z0-9_\/.:=@%+,-]+$/;
/** POSIX shell quoting (safe words are left bare, so logs stay readable). */
export function shq(s: string): string {
  return SAFE.test(s) ? s : `'${s.replace(/'/g, `'\\''`)}'`;
}

export interface SshSession {
  readonly host: string;
  /** Runs a remote command; a non-zero exit throws. */
  run(remote: string): Promise<void>;
  /** Runs a remote command and returns its stdout. */
  text(remote: string): Promise<string>;
  /** `scp -r` a local file or directory to a remote path. */
  copy(local: string, remote: string): Promise<void>;
  /** The ssh invocation as a shell command, for pipes and redirects: `ctx.sh(`${s.shell("cat > f")} < ${file}`)`. */
  shell(remote: string): string;
}

/**
 * Opens an SSH "session" to `host` (`user@host`) with a private key: installs openssh-client (apk),
 * writes the key to `<tmpDir>/ssh/id`. The host key is not verified: hosts are reached over the tailnet.
 */
export async function sshSession(ctx: Pick<Ctx, "exec" | "sh" | "tmpDir">, opts: { host: string; key: string }): Promise<SshSession> {
  await ctx.exec(["apk", "add", "--no-cache", "openssh-client"]);
  const dir = join(ctx.tmpDir, "ssh");
  mkdirSync(dir, { recursive: true, mode: 0o700 });
  const keyFile = join(dir, "id");
  // OpenSSH rejects a key without a trailing newline.
  writeFileSync(keyFile, opts.key.endsWith("\n") ? opts.key : opts.key + "\n", { mode: 0o600 });
  chmodSync(keyFile, 0o600);
  const sshOpts = ["-i", keyFile, "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null", "-o", "LogLevel=ERROR", "-o", "BatchMode=yes"];
  const argv = (remote: string) => ["ssh", ...sshOpts, opts.host, remote];
  return {
    host: opts.host,
    run: (remote) => ctx.exec(argv(remote)),
    text: (remote) => ctx.sh.text(argv(remote).map(shq).join(" ")),
    copy: (local, remote) => ctx.exec(["scp", ...sshOpts, "-r", local, `${opts.host}:${remote}`]),
    shell: (remote) => argv(remote).map(shq).join(" "),
  };
}
