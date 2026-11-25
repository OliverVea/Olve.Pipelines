// steps.mirrorToRepo — publish a snapshot directory to a GitHub repository as one force-pushed commit,
// after a gitleaks scan that can't be turned off.
import {
  existsSync,
  lstatSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { isAbsolute, join, relative } from 'node:path';
import type { Ctx } from '../ctx/types.ts';
import type { FileRef } from '../core/models.ts';
import type { Secret } from '../core/secrets.ts';
import { implement, type Implementation } from '../core/handlers.ts';
import { step, type LiteralName, type Step } from '../core/steps.ts';
import { MirroredCommit, Snapshot, Source } from './models.ts';
import { hostKeyOptions, knownHosts, shq, writeSshFiles } from './ssh.ts';

/** Debian, as the tools come from apt at run time. */
export const MIRROR_TO_REPO_IMAGE = 'debian:trixie-slim';

export const MIRROR_TOOLS_INSTALL =
  'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends ' +
  'ca-certificates curl git openssh-client';

export const GITLEAKS_VERSION = '8.30.1';
/** sha256 of `gitleaks_8.30.1_linux_x64.tar.gz`, from the release's checksums file. */
export const GITLEAKS_SHA256 =
  '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb';
export const GITLEAKS_URL = `https://github.com/gitleaks/gitleaks/releases/download/v${GITLEAKS_VERSION}/gitleaks_${GITLEAKS_VERSION}_linux_x64.tar.gz`;

/** GitHub's published ed25519 host key (`api.github.com/meta`). */
export const GITHUB_HOST_KEY =
  'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl';

/** gitleaks' own rules, used when the step has no `rules`. */
export const DEFAULT_GITLEAKS_CONFIG = '[extend]\nuseDefault = true\n';

const REPOSITORY = /^[\w.-]+\/[\w.-]+$/;
const BRANCH =
  /^(?![-/.]|refs\/)(?!.*(?:\.\.|\/\/|\/\.))(?!.*(?:\.lock|\/|\.)$)[\w./-]+$/;

export interface MirrorToRepoDef<N extends string, K extends string> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /** The GitHub repository to push to, `owner/name`. */
  repository: string;
  /** A deploy key with write access to `repository`. */
  deployKey: Secret<K>;
  /** The commit's author (and committer). */
  author: { name: string; email: string };
  /** github.com's public key, as `ssh-keyscan` prints it. Default: GitHub's published ed25519 key. */
  hostKey?: string;
  /**
   * A branch of `repository` holding the public repository's own files (README, LICENSE, its `.pipelines/`).
   * Its files are laid under the snapshot's and scanned with them; a file in both fails the step.
   */
  base?: string;
  /**
   * A gitleaks TOML config in the source tree (path relative to its root), used instead of gitleaks'
   * defaults; `[extend] useDefault = true` keeps them. It comes from the `Source` input, never from
   * the snapshot: it names the strings it guards, so it must not be published.
   */
  rules?: string;
  /** Rule id → a sample line that must trip that rule; checked on every run before the snapshot is scanned. */
  canaries?: Readonly<Record<string, string>>;
  timeoutSeconds?: number;
}

type SnapshotInput = { readonly snapshot: typeof Snapshot };
type RulesInput = SnapshotInput & { readonly source: typeof Source };

interface Finding {
  RuleID: string;
  File: string;
  StartLine?: number;
}

/**
 * Pushes the snapshot's files (laid over `base`'s, when given) to `repository` as one commit on `main`
 * (force-pushed, no history), after a mandatory gitleaks scan: any finding fails the step before the push.
 */
export function mirrorToRepo<const N extends string, const K extends string>(
  def: MirrorToRepoDef<N, K> & { rules: string },
): Implementation<Step<N, RulesInput, typeof MirroredCommit, K>>;
export function mirrorToRepo<const N extends string, const K extends string>(
  def: MirrorToRepoDef<N, K> & { rules?: undefined },
): Implementation<Step<N, SnapshotInput, typeof MirroredCommit, K>>;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function mirrorToRepo(def: MirrorToRepoDef<string, string>): any {
  const fail = (what: string) => new Error(`mirrorToRepo '${def.name}': ${what}`);
  if (!REPOSITORY.test(def.repository ?? ''))
    throw fail(`repository '${def.repository}' must be owner/name`);
  if (!def.author?.name || !def.author?.email)
    throw fail('author needs a name and an email');
  if (def.base !== undefined && !BRANCH.test(def.base))
    throw fail(`base '${def.base}' must be a branch name`);
  if (def.base === 'main')
    throw fail("base can't be main: that is the branch the snapshot replaces");
  const known = knownHosts(def.hostKey ?? GITHUB_HOST_KEY);
  const rules = def.rules === undefined ? undefined : relativePath(def.rules);
  if (rules === '')
    throw fail(
      `rules '${def.rules}' must be a relative file path inside the source tree`,
    );
  const canaries = Object.entries(def.canaries ?? {});
  if (canaries.length && rules === undefined)
    throw fail('canaries test custom rules: give rules too');
  for (const [id, sample] of canaries)
    if (!id || !sample) throw fail(`canary '${id}' needs a rule id and a sample line`);

  const contract = step({
    name: def.name,
    description: def.description,
    kind: 'mirrorToRepo',
    image: MIRROR_TO_REPO_IMAGE,
    input:
      rules === undefined
        ? { snapshot: Snapshot }
        : { snapshot: Snapshot, source: Source },
    output: MirroredCommit,
    secrets: [def.deployKey],
    timeoutSeconds: def.timeoutSeconds,
    settings: {
      repository: def.repository,
      deployKey: def.deployKey.name,
      author: { name: def.author.name, email: def.author.email },
      ...(def.hostKey !== undefined ? { hostKey: def.hostKey } : {}),
      ...(def.base !== undefined ? { base: def.base } : {}),
      ...(rules !== undefined ? { rules } : {}),
      ...(canaries.length ? { canaries: Object.fromEntries(canaries) } : {}),
    },
  });

  return implement(contract, async (stepInput, ctx: Ctx) => {
    const input = stepInput as unknown as {
      snapshot: { tree: FileRef; message: string };
      source?: { tree: FileRef };
    };
    const work = join(ctx.tmpDir, 'mirror');
    const repo = join(work, 'repo');
    mkdirSync(work, { recursive: true });

    const tree = await ctx.files.fetch(input.snapshot.tree);
    // A .git would push its history; a .gitleaksignore would hide findings from the scan.
    for (const name of ['.git', '.gitleaksignore'])
      if (existsSync(join(tree, name)))
        throw new Error(`the snapshot has a ${name}: give the files only`);

    let config = join(work, 'gitleaks.toml');
    if (rules !== undefined) {
      config = join(await ctx.files.fetch(input.source!.tree), rules);
      if (!existsSync(config))
        throw new Error(`rules file '${rules}' not found in the source tree`);
    } else writeFileSync(config, DEFAULT_GITLEAKS_CONFIG);

    await ctx.sh(MIRROR_TOOLS_INSTALL);
    const tgz = join(work, 'gitleaks.tgz');
    const bin = join(work, 'bin');
    mkdirSync(bin, { recursive: true });
    await ctx.exec(['curl', '-fsSL', '-o', tgz, GITLEAKS_URL]);
    await ctx.sh(`echo ${shq(`${GITLEAKS_SHA256}  ${tgz}`)} | sha256sum -c -`);
    await ctx.exec(['tar', 'xzf', tgz, '-C', bin, 'gitleaks']);

    const scan = async (dir: string, report: string, verbose: boolean) => {
      await ctx.exec([
        join(bin, 'gitleaks'),
        'dir',
        dir,
        '--config',
        config,
        '--no-banner',
        '--redact',
        ...(verbose ? ['--verbose'] : []),
        // Nothing may silence a finding: no inline gitleaks:allow, no .gitleaksignore from the cwd.
        '--ignore-gitleaks-allow',
        '--gitleaks-ignore-path',
        join(work, 'no-gitleaksignore'),
        '--exit-code',
        '0',
        '--report-format',
        'json',
        '--report-path',
        report,
      ]);
      if (!existsSync(report)) throw new Error('gitleaks wrote no report');
      return JSON.parse(readFileSync(report, 'utf8')) as Finding[];
    };

    const ssh = () => {
      const key = (ctx.secrets as Record<string, string | undefined>)[def.deployKey.name];
      if (!key?.trim())
        throw new Error(`secret ${def.deployKey.name} (deployKey) is not set`);
      const { keyFile, knownFile } = writeSshFiles(ctx.tmpDir, key, known);
      const command = [
        'ssh',
        '-i',
        keyFile,
        '-o',
        'IdentitiesOnly=yes',
        ...hostKeyOptions(knownFile).flatMap((o) => ['-o', o]),
        '-o',
        'LogLevel=ERROR',
        '-o',
        'BatchMode=yes',
      ]
        .map(shq)
        .join(' ');
      return { GIT_SSH_COMMAND: command };
    };
    const remote = `git@github.com:${def.repository}.git`;
    const at = { cwd: repo };
    let sshEnv: { GIT_SSH_COMMAND: string } | undefined;

    if (def.base !== undefined) {
      sshEnv = ssh();
      const ref = `refs/heads/${def.base}`;
      mkdirSync(repo, { recursive: true });
      await ctx.exec(['git', 'init', '-q'], at);
      try {
        await ctx.exec(['git', 'ls-remote', '-q', '--exit-code', remote, ref], {
          ...at,
          env: sshEnv,
        });
      } catch (e) {
        // --exit-code: 2 means the remote answered and has no such ref.
        if ((e as { exitCode?: unknown }).exitCode === 2)
          throw new Error(`branch '${def.base}' not found in ${def.repository}`);
        throw e;
      }
      await ctx.exec(['git', 'fetch', '-q', '--depth', '1', remote, ref], {
        ...at,
        env: sshEnv,
      });
      await ctx.exec(['git', 'checkout', '-q', 'FETCH_HEAD'], at);
      rmSync(join(repo, '.git'), { recursive: true, force: true });
      if (existsSync(join(repo, '.gitleaksignore')))
        throw new Error(`branch '${def.base}' has a .gitleaksignore: remove it`);
      const both = overlap(repo, tree);
      if (both.length)
        throw new Error(
          `the snapshot and branch '${def.base}' both have: ` +
            (both.length > 10 ? [...both.slice(0, 10), '…'] : both).join(', ') +
            ': keep each file in one of them',
        );
    }

    if (canaries.length) {
      const dir = join(work, 'canaries');
      mkdirSync(dir, { recursive: true });
      canaries.forEach(([, sample], i) =>
        writeFileSync(join(dir, `canary-${i}.txt`), sample + '\n'),
      );
      ctx.log('checking the rules on their canaries', { rules: canaries.length });
      const fired = new Set(
        (await scan(dir, join(work, 'canaries.json'), false)).map((f) => f.RuleID),
      );
      const silent = canaries.filter(([id]) => !fired.has(id));
      if (silent.length)
        throw new Error(
          silent
            .map(
              ([id]) =>
                `rule '${id}' didn't fire on its canary: check its regex and keywords (keywords must be lowercase)`,
            )
            .join('\n'),
        );
    }

    await ctx.exec(['cp', '-a', `${tree}/.`, repo]);
    const scanned =
      def.base === undefined ? 'the snapshot' : `the snapshot and branch '${def.base}'`;
    ctx.log(`scanning ${scanned} for secrets`);
    const findings = await scan(repo, join(work, 'findings.json'), true);
    if (findings.length)
      throw new Error(
        `gitleaks found ${findings.length} secret${findings.length === 1 ? '' : 's'} in ${scanned}; nothing was pushed:\n` +
          findings
            .map((f) => {
              const file = isAbsolute(f.File) ? relative(repo, f.File) : f.File;
              return `  ${f.RuleID} in ${file}${f.StartLine ? `:${f.StartLine}` : ''}`;
            })
            .join('\n'),
      );

    const message = join(work, 'message.txt');
    writeFileSync(message, input.snapshot.message);
    sshEnv ??= ssh();
    await ctx.exec(['git', 'init', '-q', '-b', 'main'], at);
    await ctx.exec(['git', 'add', '-A'], at);
    await ctx.exec(
      [
        'git',
        '-c',
        `user.name=${def.author.name}`,
        '-c',
        `user.email=${def.author.email}`,
        'commit',
        '-q',
        '-F',
        message,
      ],
      at,
    );
    const commit = await ctx.sh.text('git rev-parse HEAD', at);
    ctx.log('pushing', { repository: def.repository, commit });
    await ctx.exec(['git', 'push', '-q', '--force', remote, 'main'], {
      ...at,
      env: sshEnv,
    });
    ctx.link(`https://github.com/${def.repository}`, def.repository);
    return { repository: def.repository, commit };
  }) as never;
}

/** `path` without `./` or a trailing `/`; '' when it is absolute, climbs out, or names the root. */
function relativePath(path: string): string {
  const clean = path.replace(/^\.\/+/, '').replace(/\/+$/, '');
  if (clean.startsWith('/') || clean.split('/').includes('..') || clean === '.')
    return '';
  return clean;
}

/** Paths that are a file (or a link) in one tree and exist in the other, sorted. */
function overlap(a: string, b: string): string[] {
  const out: string[] = [];
  const walk = (rel: string) => {
    for (const name of readdirSync(join(b, rel))) {
      const path = rel ? `${rel}/${name}` : name;
      let inA;
      try {
        inA = lstatSync(join(a, path));
      } catch {
        continue;
      }
      if (inA.isDirectory() && lstatSync(join(b, path)).isDirectory()) walk(path);
      else out.push(path);
    }
  };
  walk('');
  return out.sort();
}
