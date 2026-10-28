import {
  chmodSync,
  copyFileSync,
  cpSync,
  existsSync,
  mkdirSync,
  readFileSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import { CliError } from '../errors';
import { diagnostic, succeeded, type ProcessRunner, type ProcessSpec } from '../process';
import {
  parseManifest,
  str,
  type DevManifest,
  type DevStep,
  type JsonObject,
} from './manifest';
import { ATTEMPT, Pool, RUN, Workspace } from './pool';
import type { Toolchain } from './toolchain';

/** The step-pod init image, the server's default (`HarnessImage.cs`; `test/harness-image.test.ts` checks they match). */
export const HARNESS_BASE_IMAGE =
  'ghcr.io/olivervea/olve-pipelines-harness-base:src-db8d7779c04b';
export const HARNESS_BASE_ENV = 'OLVE_HARNESS_BASE';
const CASES_ENV = 'OLVE_CASES';
const LOADER = '/olve/lib/ld-linux-x86-64.so.2';
const RESULT_BEGIN = '---OLVE-RESULT-BEGIN---';
const RESULT_END = '---OLVE-RESULT-END---';
/** The pod's upload channel, which a local run must never inherit. */
const PRIVATE_ENV = [
  'OLVE_RESULT_URL',
  'OLVE_FILE_URLS',
  'OLVE_FILES_BASE_URL',
  'OLVE_FILES_TOKEN',
];

/** Where `pl dev` writes: status and the child's stderr to stderr; the child's stdout to stdout, or stderr with --json. */
export type DevConsole = {
  json: boolean;
  out(line: string): void;
  err(line: string): void;
};

export const status = (c: DevConsole, message: string) => c.err(`[pl dev] ${message}`);

export type DescribeOutcome = {
  manifest?: DevManifest;
  exitCode: number;
  problems: string;
};
export type StepOutcome = { envelope?: JsonObject; exitCode: number };
export const outcomeStatus = (o: StepOutcome) => str(o.envelope?.status) ?? 'no result';

export type RunOptions = {
  secretOverrides: Record<string, string>;
  docker: boolean;
  warnUndeclaredSecrets: boolean;
  config?: JsonObject;
};

export class DevSession {
  constructor(
    private readonly run: ProcessRunner,
    private readonly tools: Toolchain,
    private readonly pipelineDir: string,
    readonly workspace: Workspace,
    private readonly console: DevConsole,
    private readonly env: Record<string, string | undefined>,
    private readonly signal?: AbortSignal,
  ) {}

  async describe(): Promise<DescribeOutcome> {
    const r = await this.run(
      {
        command: this.tools.bun,
        args: [this.tools.devCli, 'describe', this.pipelineDir],
      },
      this.signal,
    );
    if (!succeeded(r)) return { exitCode: r.exitCode, problems: diagnostic(r) };
    if (r.stderr.trim()) this.console.err(r.stderr.trim());
    return { manifest: parseManifest(r.stdout), exitCode: 0, problems: '' };
  }

  writeInput(input: JsonObject): void {
    mkdirSync(this.workspace.root, { recursive: true });
    writeFileSync(this.workspace.inputJson, JSON.stringify(input, null, 2) + '\n');
  }

  copyInput(file: string): void {
    mkdirSync(this.workspace.root, { recursive: true });
    copyFileSync(file, this.workspace.inputJson);
  }

  async runStep(
    manifest: DevManifest,
    step: DevStep,
    pool: Pool,
    options: RunOptions,
    cases: string[] = [],
  ): Promise<StepOutcome> {
    const secrets = this.secretEnvironment(
      manifest,
      step,
      options.secretOverrides,
      options.warnUndeclaredSecrets,
    );
    if (options.config) this.addConfigToInput(options.config);
    const casesJson = cases.length ? JSON.stringify(cases) : undefined;
    mkdirSync(this.workspace.poolFiles, { recursive: true });
    rmSync(this.workspace.outputJson, { force: true }); // a usage or config failure writes none: never read a stale one
    const exitCode = options.docker
      ? await this.runInDocker(manifest, step, secrets, casesJson)
      : await this.runFromSource(manifest, step, secrets, casesJson);
    const envelope = this.readEnvelope();
    pool.record(step.name, envelope, this.workspace);
    return { envelope, exitCode };
  }

  /** As the service does: the step reads its config from `$config` in input.json. */
  private addConfigToInput(config: JsonObject): void {
    let input: JsonObject = {};
    if (existsSync(this.workspace.inputJson)) {
      const parsed = JSON.parse(readFileSync(this.workspace.inputJson, 'utf8'));
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) input = parsed;
    }
    input.$config = structuredClone(config);
    writeFileSync(this.workspace.inputJson, JSON.stringify(input));
  }

  private async runFromSource(
    manifest: DevManifest,
    step: DevStep,
    secrets: Record<string, string | null>,
    casesJson?: string,
  ): Promise<number> {
    status(this.console, `running '${step.name}' from source`);
    const env = {
      ...harnessEnvironment(
        manifest,
        this.workspace.root,
        this.workspace.tmp,
        this.workspace.poolFiles,
      ),
      [CASES_ENV]: casesJson ?? null,
      ...secrets,
    };
    const r = await this.run(
      {
        command: this.tools.bun,
        args: [
          this.tools.devCli,
          'run',
          this.pipelineDir,
          step.name,
          '--olve-dir',
          this.workspace.root,
        ],
        env,
        onStdoutLine: this.resultFilter(),
        onStderrLine: (line) => this.console.err(line),
      },
      this.signal,
    );
    return r.exitCode;
  }

  /** Compiled with olve-build and run in the step's image like the pod: the /olve layout, the bundled loader, the same env. */
  private async runInDocker(
    manifest: DevManifest,
    step: DevStep,
    secrets: Record<string, string | null>,
    casesJson?: string,
  ): Promise<number> {
    await this.build();
    const libDir = await this.loaderLibs();
    this.stageDockerRoot(libDir);
    const args = ['create', '--entrypoint', LOADER];
    for (const [k, v] of Object.entries(
      harnessEnvironment(manifest, '/olve', '/olve/tmp', '/olve-pool'),
    ))
      if (v !== null) args.push('-e', `${k}=${v}`);
    if (casesJson) args.push('-e', `${CASES_ENV}=${casesJson}`);
    // Secrets by name only (`-e NAME`): docker reads the values from its own environment, so they never reach a command line.
    for (const name of step.secrets)
      if (name in secrets || this.env[name] !== undefined) args.push('-e', name);
    args.push(
      '-v',
      `${this.workspace.poolFiles}:/olve-pool:ro`,
      step.image,
      '--library-path',
      '/olve/lib',
      '/olve/step',
      'run',
      step.name,
    );
    status(this.console, `running '${step.name}' in ${step.image}`);
    const created = await this.run(
      { command: 'docker', args, env: secrets, onStderrLine: (l) => this.console.err(l) },
      this.signal,
    );
    if (!succeeded(created))
      throw new CliError(`docker create failed (exit ${created.exitCode}).`);
    const container = created.stdout.trim();
    try {
      // `docker cp`, not a bind mount: an emptyDir-like /olve, and work/ comes back owned by the caller even when the image runs as root.
      const copyIn = await this.run(
        {
          command: 'docker',
          args: ['cp', `${this.workspace.dockerStaging}/.`, `${container}:/olve`],
        },
        this.signal,
      );
      if (!succeeded(copyIn))
        throw new CliError(`docker cp into the container failed: ${diagnostic(copyIn)}`);
      const started = await this.run(
        {
          command: 'docker',
          args: ['start', '--attach', container],
          env: secrets,
          onStdoutLine: this.resultFilter(),
          onStderrLine: (l) => this.console.err(l),
        },
        this.signal,
      );
      rmSync(this.workspace.work, { recursive: true, force: true });
      mkdirSync(this.workspace.work, { recursive: true });
      const copyOut = await this.run({
        command: 'docker',
        args: ['cp', `${container}:/olve/work/.`, this.workspace.work],
      });
      if (!succeeded(copyOut))
        status(
          this.console,
          `could not copy /olve/work out of the container: ${diagnostic(copyOut)}`,
        );
      return started.exitCode;
    } finally {
      await this.run({ command: 'docker', args: ['rm', '--force', container] });
    }
  }

  private async build(): Promise<void> {
    status(this.console, `compiling with olve-build into ${this.workspace.build}`);
    const r = await this.run(
      {
        command: this.tools.bun,
        args: [this.tools.buildCli, this.pipelineDir, '--out', this.workspace.build],
        onStdoutLine: (l) => this.console.err(l),
        onStderrLine: (l) => this.console.err(l),
      },
      this.signal,
    );
    if (!succeeded(r))
      throw new CliError(`olve-build failed (exit ${r.exitCode}); see the output above.`);
    if (!existsSync(join(this.workspace.build, 'step')))
      throw new CliError(
        `olve-build produced no '${join(this.workspace.build, 'step')}'.`,
      );
  }

  /** The glibc loader and libs from the harness image, cached per image tag. */
  private async loaderLibs(): Promise<string> {
    const image = this.env[HARNESS_BASE_ENV] || HARNESS_BASE_IMAGE;
    const tag = image.slice(Math.max(image.lastIndexOf(':'), image.lastIndexOf('@')) + 1);
    const libDir = this.workspace.libCache(tag);
    if (existsSync(join(libDir, 'ld-linux-x86-64.so.2'))) return libDir;
    status(this.console, `extracting the loader libs from ${image}`);
    const created = await this.run(
      {
        command: 'docker',
        args: ['create', image],
        onStderrLine: (l) => this.console.err(l),
      },
      this.signal,
    );
    if (!succeeded(created))
      throw new CliError(`docker create ${image} failed (exit ${created.exitCode}).`);
    const container = created.stdout.trim();
    const partial = `${libDir}.partial`;
    try {
      rmSync(partial, { recursive: true, force: true });
      mkdirSync(dirname(partial), { recursive: true });
      const copied = await this.run(
        { command: 'docker', args: ['cp', `${container}:/opt/olve/lib`, partial] },
        this.signal,
      );
      if (!succeeded(copied))
        throw new CliError(
          `Could not copy /opt/olve/lib out of ${image}: ${diagnostic(copied)}`,
        );
      rmSync(libDir, { recursive: true, force: true });
      renameSync(partial, libDir);
      return libDir;
    } finally {
      await this.run({ command: 'docker', args: ['rm', '--force', container] });
    }
  }

  private stageDockerRoot(libDir: string): void {
    const root = this.workspace.dockerStaging;
    rmSync(root, { recursive: true, force: true });
    mkdirSync(root, { recursive: true });
    copyFileSync(join(this.workspace.build, 'step'), join(root, 'step'));
    cpSync(libDir, join(root, 'lib'), { recursive: true });
    if (existsSync(this.workspace.inputJson))
      copyFileSync(this.workspace.inputJson, join(root, 'input.json'));
    if (process.platform === 'win32') return;
    chmodSync(root, 0o755);
    chmodSync(join(root, 'step'), 0o755);
    // 1777 like olve-init, so images that run as non-root can write.
    for (const dir of ['work', join('work', 'files'), 'tmp']) {
      mkdirSync(join(root, dir), { recursive: true });
      chmodSync(join(root, dir), 0o1777);
    }
  }

  /** The step's declared secrets (overrides first, else the environment); every other pipeline secret unset. */
  secretEnvironment(
    manifest: DevManifest,
    step: DevStep,
    overrides: Record<string, string>,
    warnUndeclared = true,
  ): Record<string, string | null> {
    const env: Record<string, string | null> = {};
    for (const { name } of manifest.secrets)
      if (!step.secrets.includes(name)) env[name] = null;
    if (warnUndeclared)
      for (const name of Object.keys(overrides))
        if (!step.secrets.includes(name))
          status(
            this.console,
            `warning: --secret ${name} ignored: '${step.name}' does not declare it`,
          );
    const missing: string[] = [];
    for (const name of step.secrets) {
      if (name in overrides) env[name] = overrides[name]!;
      else if (this.env[name] === undefined) missing.push(name);
    }
    if (missing.length)
      status(
        this.console,
        `warning: '${step.name}' declares secret(s) ${missing.join(', ')} but they are not set; export them or pass --secret NAME=value (the step fails if it reads one)`,
      );
    return env;
  }

  /** The child's stdout without the result block (the envelope is read from output.json). */
  private resultFilter(): (line: string) => void {
    let inBlock = false;
    return (line) => {
      if (line === RESULT_BEGIN) inBlock = true;
      else if (line === RESULT_END) inBlock = false;
      else if (!inBlock) (this.console.json ? this.console.err : this.console.out)(line);
    };
  }

  private readEnvelope(): JsonObject | undefined {
    if (!existsSync(this.workspace.outputJson)) return undefined;
    try {
      const parsed = JSON.parse(readFileSync(this.workspace.outputJson, 'utf8'));
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed)
        ? parsed
        : undefined;
    } catch {
      return undefined;
    }
  }
}

function harnessEnvironment(
  manifest: DevManifest,
  olveDir: string,
  tmp: string,
  filesDir: string,
): Record<string, string | null> {
  const env: Record<string, string | null> = {
    OLVE_DIR: olveDir,
    TMPDIR: tmp,
    OLVE_FILES_DIR: filesDir,
    OLVE_PIPELINE: manifest.name,
    OLVE_RUN_ID: 'local',
    OLVE_RUN: String(RUN),
    OLVE_ATTEMPT: String(ATTEMPT),
    OLVE_VERSION_STAMP: '0.0.0-local',
  };
  for (const name of PRIVATE_ENV) env[name] = null;
  return env;
}

export type { ProcessSpec };
