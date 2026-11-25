// steps.mirrorToRepo through testHandler with faked ctx parts; the assertions pin the exact commands.
import { afterAll, describe, expect, test } from 'bun:test';
import { existsSync, mkdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { secret, steps, type AnyStep, type Ctx, type FileRef } from '../../src/index.ts';
import { recordConfigRefs } from '../../src/core/config-refs.ts';
import { CommandError } from '../../src/ctx/sh.ts';
import { testFileRef, testHandler } from '../../src/testing/index.ts';
import { SHA, tmp, removeTmpDirs } from './helpers.ts';

afterAll(removeTmpDirs);

const DEPLOY_KEY = secret('MIRROR_DEPLOY_KEY', 'Deploy key of the public mirror.');
const KEY_VALUE =
  '-----BEGIN OPENSSH PRIVATE KEY-----\nmirror-key-material\n-----END OPENSSH PRIVATE KEY-----';
const GITHUB_KEY =
  'ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl';
const RULE =
  "[extend]\nuseDefault = true\n[[rules]]\nid = \"private-host\"\nregex = '''-private\\.example\\.com'''\nkeywords = [\"-private.example.com\"]\n";

const def = {
  name: 'publish',
  description: '',
  repository: 'example/public-mirror',
  deployKey: DEPLOY_KEY,
  author: { name: 'Mirror Bot', email: 'mirror@example.com' },
  rules: '.mirror/gitleaks.toml',
  canaries: { 'private-host': 'see https://pipelines-private.example.com' },
} as const;

const input = {
  snapshot: {
    tree: testFileRef({ step: 'snapshot', path: 'tree/' }),
    message: 'Mirror 2026.11.17.3',
  },
  source: {
    repo: 'example/app',
    sha: SHA,
    tree: testFileRef({ step: 'source', path: 'src/' }),
  },
};

type Call = { argv: string[] | string; env?: Record<string, string>; cwd?: string };

const writeFiles = (dir: string, files: readonly string[]) => {
  for (const f of files)
    (mkdirSync(join(dir, f, '..'), { recursive: true }),
      writeFileSync(join(dir, f), 'x'));
};

/**
 * Fakes the step's world: `fired` = rule ids the canary scan reports, `findings` = the snapshot scan's report,
 * `baseFiles` = what checking out the base branch writes, `baseMissing` = the remote has no such branch.
 */
const fakes = (
  o: {
    fired?: string[];
    findings?: object[];
    snapshotFiles?: string[];
    baseFiles?: string[];
    baseMissing?: boolean;
  } = {},
) => {
  const snapshot = tmp('mirror-snapshot');
  writeFiles(snapshot, o.snapshotFiles ?? ['README.md']);
  const source = tmp('mirror-source');
  mkdirSync(join(source, '.mirror'));
  writeFileSync(join(source, '.mirror/gitleaks.toml'), RULE);
  const calls: Call[] = [];
  const ctx: Partial<Ctx<'MIRROR_DEPLOY_KEY'>> = {
    files: {
      fetch: async (ref: FileRef) => (ref.path === 'tree/' ? snapshot : source),
    },
    exec: async (argv, opts) => {
      calls.push({ argv: [...argv], ...opts });
      if (argv[1] === 'ls-remote' && o.baseMissing)
        throw new CommandError('`git ls-remote`', 2, null);
      if (argv[1] === 'checkout') {
        mkdirSync(join(opts!.cwd!, '.git'), { recursive: true });
        writeFiles(
          opts!.cwd!,
          o.baseFiles ?? ['README.md', 'LICENSE', '.pipelines/pipeline.ts'],
        );
      }
      if (!argv[0]!.endsWith('/gitleaks')) return;
      const report = argv[argv.indexOf('--report-path') + 1]!;
      const scanned = argv[2]!;
      const found = scanned.endsWith('/canaries')
        ? (o.fired ?? ['private-host']).map((RuleID) => ({
            RuleID,
            File: join(scanned, 'canary-0.txt'),
          }))
        : (o.findings ?? []).map((x: { File?: string }) => ({
            ...x,
            File: join(scanned, x.File!),
          }));
      writeFileSync(report, JSON.stringify(found));
    },
    sh: Object.assign(
      async (cmd: string | { script: string }, opts?: object) =>
        void calls.push({ argv: cmd as string, ...opts }),
      {
        text: async (cmd: string | { script: string }, opts?: object) => {
          calls.push({ argv: cmd as string, ...opts });
          return 'f'.repeat(40);
        },
      },
    ),
  };
  return { snapshot, source, calls, ctx };
};

const run = (
  f: ReturnType<typeof fakes>,
  step: AnyStep = steps.mirrorToRepo(def),
  stepInput: object = input,
) =>
  testHandler(step as never, stepInput as never, {
    secrets: { MIRROR_DEPLOY_KEY: KEY_VALUE } as never,
    ctx: f.ctx as never,
  });

describe('steps.mirrorToRepo', () => {
  test('scans the canaries, then the snapshot, then pushes one commit', async () => {
    const step = steps.mirrorToRepo(def);
    expect(step).toMatchObject({ image: 'debian:trixie-slim', secrets: [DEPLOY_KEY] });
    expect(Object.keys(step.input)).toEqual(['snapshot', 'source']);
    expect(recordConfigRefs(step.settings)).toEqual({
      repository: 'example/public-mirror',
      deployKey: 'MIRROR_DEPLOY_KEY',
      author: { name: 'Mirror Bot', email: 'mirror@example.com' },
      rules: '.mirror/gitleaks.toml',
      canaries: { 'private-host': 'see https://pipelines-private.example.com' },
    });
    const f = fakes();
    const r = await run(f, step);
    expect(r).toMatchObject({
      status: 'succeeded',
      output: { repository: 'example/public-mirror', commit: 'f'.repeat(40) },
    });
    expect(r.links).toEqual([
      { url: 'https://github.com/example/public-mirror', label: 'example/public-mirror' },
    ]);

    const work = join(r.dir, 'tmp', 'mirror');
    const repo = join(work, 'repo');
    const key = join(r.dir, 'tmp', 'ssh', 'id');
    const known = join(r.dir, 'tmp', 'ssh', 'known_hosts');
    expect(readFileSync(key, 'utf8')).toBe(KEY_VALUE + '\n');
    expect(statSync(key).mode & 0o777).toBe(0o600);
    expect(readFileSync(known, 'utf8')).toBe(`olve-ssh-host ${GITHUB_KEY}\n`);
    expect(readFileSync(join(work, 'canaries', 'canary-0.txt'), 'utf8')).toBe(
      'see https://pipelines-private.example.com\n',
    );
    expect(readFileSync(join(work, 'message.txt'), 'utf8')).toBe('Mirror 2026.11.17.3');
    const gitleaks = (dir: string, report: string, verbose: boolean) => [
      join(work, 'bin', 'gitleaks'),
      'dir',
      dir,
      '--config',
      join(f.source, '.mirror/gitleaks.toml'),
      '--no-banner',
      '--redact',
      ...(verbose ? ['--verbose'] : []),
      '--ignore-gitleaks-allow',
      '--gitleaks-ignore-path',
      join(work, 'no-gitleaksignore'),
      '--exit-code',
      '0',
      '--report-format',
      'json',
      '--report-path',
      join(work, report),
    ];
    const tgz = join(work, 'gitleaks.tgz');
    expect(f.calls).toEqual([
      {
        argv:
          'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends ' +
          'ca-certificates curl git openssh-client',
      },
      {
        argv: [
          'curl',
          '-fsSL',
          '-o',
          tgz,
          'https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz',
        ],
      },
      {
        argv: `echo '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb  ${tgz}' | sha256sum -c -`,
      },
      { argv: ['tar', 'xzf', tgz, '-C', join(work, 'bin'), 'gitleaks'] },
      { argv: gitleaks(join(work, 'canaries'), 'canaries.json', false) },
      { argv: ['cp', '-a', `${f.snapshot}/.`, repo] },
      { argv: gitleaks(repo, 'findings.json', true) },
      { argv: ['git', 'init', '-q', '-b', 'main'], cwd: repo },
      { argv: ['git', 'add', '-A'], cwd: repo },
      {
        argv: [
          'git',
          '-c',
          'user.name=Mirror Bot',
          '-c',
          'user.email=mirror@example.com',
          'commit',
          '-q',
          '-F',
          join(work, 'message.txt'),
        ],
        cwd: repo,
      },
      { argv: 'git rev-parse HEAD', cwd: repo },
      {
        argv: [
          'git',
          'push',
          '-q',
          '--force',
          'git@github.com:example/public-mirror.git',
          'main',
        ],
        cwd: repo,
        env: {
          GIT_SSH_COMMAND:
            `ssh -i ${key} -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile=${known} ` +
            `-o GlobalKnownHostsFile=/dev/null -o HostKeyAlias=olve-ssh-host -o LogLevel=ERROR -o BatchMode=yes`,
        },
      },
    ]);
    // The key travels only as a file.
    expect(JSON.stringify(f.calls)).not.toContain('mirror-key-material');
  });

  test('without rules: gitleaks defaults from a config of the step, and no source input', async () => {
    const { rules: _, canaries: __, ...plain } = def;
    const step = steps.mirrorToRepo(plain);
    expect(Object.keys(step.input)).toEqual(['snapshot']);
    const f = fakes();
    const r = await run(f, step, { snapshot: input.snapshot });
    expect(r.status).toBe('succeeded');
    const config = join(r.dir, 'tmp', 'mirror', 'gitleaks.toml');
    expect(readFileSync(config, 'utf8')).toBe('[extend]\nuseDefault = true\n');
    const scans = f.calls.filter(
      (c) => Array.isArray(c.argv) && c.argv[0]!.endsWith('/gitleaks'),
    );
    expect(scans.map((c) => (c.argv as string[])[4])).toEqual([config]);
  });

  test.each([
    [
      'findings fail the step, naming rule and file only',
      {
        findings: [
          { RuleID: 'private-key', File: 'keys/id', StartLine: 1, Secret: 'REDACTED' },
          { RuleID: 'private-host', File: 'docs/setup.md', StartLine: 12 },
        ],
      },
      'gitleaks found 2 secrets in the snapshot; nothing was pushed:\n  private-key in keys/id:1\n  private-host in docs/setup.md:12',
      'snapshot',
    ],
    [
      "a canary that doesn't fire fails before the snapshot is scanned",
      { fired: [] },
      "rule 'private-host' didn't fire on its canary: check its regex and keywords (keywords must be lowercase)",
      'canaries',
    ],
    [
      'a snapshot with a .git is refused',
      { snapshotFiles: ['.git/HEAD'] },
      'the snapshot has a .git: give the files only',
      'none',
    ],
    [
      'a snapshot with a .gitleaksignore is refused',
      { snapshotFiles: ['.gitleaksignore'] },
      'the snapshot has a .gitleaksignore: give the files only',
      'none',
    ],
  ] as const)('%s', async (_, o, message, scannedLast) => {
    const f = fakes(o as unknown as Parameters<typeof fakes>[0]);
    const r = await run(f);
    expect(r).toMatchObject({
      status: 'failed',
      error: { kind: 'handler', message: `step 'publish' failed: ${message}` },
    });
    const argvs = f.calls.map((c) => c.argv);
    const scanned = argvs
      .filter((a) => Array.isArray(a) && a[0]!.endsWith('/gitleaks'))
      .map((a) => (a[2]!.endsWith('/canaries') ? 'canaries' : 'snapshot'));
    expect(scanned.at(-1) ?? 'none').toBe(scannedLast);
    expect(argvs.some((a) => Array.isArray(a) && a[0] === 'git')).toBe(false);
    expect(existsKey(r.dir)).toBe(false);
  });

  describe('with base', () => {
    const { rules: _, canaries: __, ...plain } = def;
    const withBase = { ...plain, base: 'public' };
    const baseInput = { snapshot: input.snapshot };

    test('fetches the branch, lays the snapshot over it, scans the whole tree, then pushes', async () => {
      const step = steps.mirrorToRepo({ ...def, base: 'public' });
      expect(recordConfigRefs(step.settings)).toMatchObject({ base: 'public' });
      const f = fakes({ snapshotFiles: ['index.md', '.pipelines/site.ts'] });
      const r = await run(f, step);
      expect(r.status).toBe('succeeded');

      const work = join(r.dir, 'tmp', 'mirror');
      const repo = join(work, 'repo');
      const tgz = join(work, 'gitleaks.tgz');
      const ssh = {
        GIT_SSH_COMMAND:
          `ssh -i ${join(r.dir, 'tmp', 'ssh', 'id')} -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes ` +
          `-o UserKnownHostsFile=${join(r.dir, 'tmp', 'ssh', 'known_hosts')} ` +
          `-o GlobalKnownHostsFile=/dev/null -o HostKeyAlias=olve-ssh-host -o LogLevel=ERROR -o BatchMode=yes`,
      };
      const remote = 'git@github.com:example/public-mirror.git';
      const gitleaks = (dir: string, report: string, verbose: boolean) => [
        join(work, 'bin', 'gitleaks'),
        'dir',
        dir,
        '--config',
        join(f.source, '.mirror/gitleaks.toml'),
        '--no-banner',
        '--redact',
        ...(verbose ? ['--verbose'] : []),
        '--ignore-gitleaks-allow',
        '--gitleaks-ignore-path',
        join(work, 'no-gitleaksignore'),
        '--exit-code',
        '0',
        '--report-format',
        'json',
        '--report-path',
        join(work, report),
      ];
      expect(f.calls).toEqual([
        {
          argv:
            'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends ' +
            'ca-certificates curl git openssh-client',
        },
        {
          argv: [
            'curl',
            '-fsSL',
            '-o',
            tgz,
            'https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz',
          ],
        },
        {
          argv: `echo '551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb  ${tgz}' | sha256sum -c -`,
        },
        { argv: ['tar', 'xzf', tgz, '-C', join(work, 'bin'), 'gitleaks'] },
        { argv: ['git', 'init', '-q'], cwd: repo },
        {
          argv: ['git', 'ls-remote', '-q', '--exit-code', remote, 'refs/heads/public'],
          cwd: repo,
          env: ssh,
        },
        {
          argv: ['git', 'fetch', '-q', '--depth', '1', remote, 'refs/heads/public'],
          cwd: repo,
          env: ssh,
        },
        { argv: ['git', 'checkout', '-q', 'FETCH_HEAD'], cwd: repo },
        { argv: gitleaks(join(work, 'canaries'), 'canaries.json', false) },
        { argv: ['cp', '-a', `${f.snapshot}/.`, repo] },
        { argv: gitleaks(repo, 'findings.json', true) },
        { argv: ['git', 'init', '-q', '-b', 'main'], cwd: repo },
        { argv: ['git', 'add', '-A'], cwd: repo },
        {
          argv: [
            'git',
            '-c',
            'user.name=Mirror Bot',
            '-c',
            'user.email=mirror@example.com',
            'commit',
            '-q',
            '-F',
            join(work, 'message.txt'),
          ],
          cwd: repo,
        },
        { argv: 'git rev-parse HEAD', cwd: repo },
        { argv: ['git', 'push', '-q', '--force', remote, 'main'], cwd: repo, env: ssh },
      ]);
      // The base branch's history is dropped: only its files reach the new commit.
      expect(existsSync(join(repo, '.git'))).toBe(false);
      expect(existsSync(join(repo, 'LICENSE'))).toBe(true);
      expect(JSON.stringify(f.calls)).not.toContain('mirror-key-material');
    });

    test.each([
      [
        'a file in both fails before the snapshot is scanned',
        { snapshotFiles: ['README.md', 'LICENSE', '.pipelines/site.ts'] },
        "the snapshot and branch 'public' both have: LICENSE, README.md: keep each file in one of them",
      ],
      [
        'a long overlap is cut after ten paths',
        {
          baseFiles: Array.from(
            { length: 12 },
            (_, i) => `f${String(i).padStart(2, '0')}`,
          ),
          snapshotFiles: Array.from(
            { length: 12 },
            (_, i) => `f${String(i).padStart(2, '0')}`,
          ),
        },
        "the snapshot and branch 'public' both have: f00, f01, f02, f03, f04, f05, f06, f07, f08, f09, …: keep each file in one of them",
      ],
      [
        'a .gitleaksignore on the branch is refused',
        { baseFiles: ['README.md', '.gitleaksignore'], snapshotFiles: ['index.md'] },
        "branch 'public' has a .gitleaksignore: remove it",
      ],
      [
        'a missing branch fails',
        { baseMissing: true },
        "branch 'public' not found in example/public-mirror",
      ],
      [
        'findings in either tree fail the step',
        {
          snapshotFiles: ['index.md'],
          findings: [{ RuleID: 'private-key', File: 'LICENSE', StartLine: 3 }],
        },
        "gitleaks found 1 secret in the snapshot and branch 'public'; nothing was pushed:\n  private-key in LICENSE:3",
      ],
    ] as const)('%s', async (_, o, message) => {
      const f = fakes(o as unknown as Parameters<typeof fakes>[0]);
      const r = await run(f, steps.mirrorToRepo(withBase), baseInput);
      expect(r).toMatchObject({
        status: 'failed',
        error: { kind: 'handler', message: `step 'publish' failed: ${message}` },
      });
      const argvs = f.calls.map((c) => c.argv);
      expect(argvs.some((a) => a[0] === 'git' && a[1] === 'push')).toBe(false);
      if (!('findings' in o))
        expect(argvs.some((a) => Array.isArray(a) && a[0]!.endsWith('/gitleaks'))).toBe(
          false,
        );
    });
  });

  test.each([
    [
      { repository: 'public-mirror' },
      "mirrorToRepo 'publish': repository 'public-mirror' must be owner/name",
    ],
    [
      { repository: 'https://github.com/example/m' },
      "mirrorToRepo 'publish': repository 'https://github.com/example/m' must be owner/name",
    ],
    [
      { author: { name: 'Mirror Bot', email: '' } },
      "mirrorToRepo 'publish': author needs a name and an email",
    ],
    [
      { rules: '../rules.toml' },
      "mirrorToRepo 'publish': rules '../rules.toml' must be a relative file path inside the source tree",
    ],
    [
      { rules: undefined },
      "mirrorToRepo 'publish': canaries test custom rules: give rules too",
    ],
    [
      { canaries: { 'private-host': '' } },
      "mirrorToRepo 'publish': canary 'private-host' needs a rule id and a sample line",
    ],
    [
      { hostKey: 'github.com' },
      "hostKey: 'github.com' is not a public key; give it as ssh-keyscan prints it, e.g. 'ssh-ed25519 AAAA…'",
    ],
    [
      { base: 'main' },
      "mirrorToRepo 'publish': base can't be main: that is the branch the snapshot replaces",
    ],
    [{ base: '-x' }, "mirrorToRepo 'publish': base '-x' must be a branch name"],
    [{ base: 'a..b' }, "mirrorToRepo 'publish': base 'a..b' must be a branch name"],
    [
      { base: 'site pages' },
      "mirrorToRepo 'publish': base 'site pages' must be a branch name",
    ],
    [{ base: '' }, "mirrorToRepo 'publish': base '' must be a branch name"],
    [
      { base: 'refs/heads/public' },
      "mirrorToRepo 'publish': base 'refs/heads/public' must be a branch name",
    ],
    [
      { base: 'site/.hidden' },
      "mirrorToRepo 'publish': base 'site/.hidden' must be a branch name",
    ],
  ])('definition error %#', (patch, message) => {
    expect(() => steps.mirrorToRepo({ ...def, ...patch } as never)).toThrow(message);
  });
});

const existsKey = (dir: string) => {
  try {
    statSync(join(dir, 'tmp', 'ssh', 'id'));
    return true;
  } catch {
    return false;
  }
};
