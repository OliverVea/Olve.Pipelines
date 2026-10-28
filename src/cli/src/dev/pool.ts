import {
  cpSync,
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { CliError, describe } from '../errors';
import { str, type Json, type JsonObject } from './manifest';

/** The run and attempt every local step pretends to be, so file refs resolve under the pool. */
export const RUN = 0;
export const ATTEMPT = 1;

export type PoolEntry = {
  status: 'succeeded' | 'skipped' | 'failed';
  output?: Json;
  reason?: string;
};

/** `<dir>/.olve`: the step's /olve layout plus the local pool of earlier steps' outputs and files. */
export class Workspace {
  readonly root: string;
  constructor(root: string) {
    this.root = resolve(root);
  }
  get inputJson() {
    return join(this.root, 'input.json');
  }
  get poolJson() {
    return join(this.root, 'pool.json');
  }
  get work() {
    return join(this.root, 'work');
  }
  get workFiles() {
    return join(this.work, 'files');
  }
  get outputJson() {
    return join(this.work, 'output.json');
  }
  get tmp() {
    return join(this.root, 'tmp');
  }
  get poolFiles() {
    return join(this.root, 'files');
  }
  get build() {
    return join(this.root, 'build');
  }
  get dockerStaging() {
    return join(this.root, 'docker');
  }
  libCache(tag: string) {
    return join(this.root, 'lib', tag);
  }
  poolFilesFor(step: string) {
    return join(this.poolFiles, String(RUN), step, String(ATTEMPT), 'files');
  }
}

/** The pipeline input and each step's last local result, kept in pool.json between runs. */
export class Pool {
  input?: JsonObject;
  steps = new Map<string, PoolEntry>();

  static load(path: string): Pool {
    const pool = new Pool();
    if (!existsSync(path)) return pool;
    let root: Json;
    try {
      root = JSON.parse(readFileSync(path, 'utf8')) as Json;
    } catch (error) {
      throw new CliError(
        `Local pool '${path}' is not valid JSON (${describe(error)}); delete it to start fresh.`,
      );
    }
    if (!root || typeof root !== 'object' || Array.isArray(root)) return pool;
    const input = root.input;
    if (input && typeof input === 'object' && !Array.isArray(input)) pool.input = input;
    const steps = root.steps;
    if (steps && typeof steps === 'object' && !Array.isArray(steps))
      for (const [name, entry] of Object.entries(steps)) {
        if (!entry || typeof entry !== 'object' || Array.isArray(entry)) continue;
        const status = str(entry.status);
        if (status !== 'succeeded' && status !== 'skipped' && status !== 'failed')
          continue;
        pool.steps.set(name, { status, output: entry.output, reason: str(entry.reason) });
      }
    return pool;
  }

  save(path: string): void {
    const steps: JsonObject = {};
    for (const [name, entry] of this.steps) {
      const e: JsonObject = { status: entry.status };
      if (entry.status === 'succeeded') e.output = entry.output ?? null;
      if (entry.reason !== undefined) e.reason = entry.reason;
      steps[name] = e;
    }
    mkdirSync(dirname(resolve(path)), { recursive: true });
    writeFileSync(
      path,
      JSON.stringify({ input: this.input ?? null, steps }, null, 2) + '\n',
    );
  }

  /** The step's envelope: its output and files on success, the reason otherwise. */
  record(step: string, envelope: JsonObject | undefined, workspace: Workspace): void {
    const target = workspace.poolFilesFor(step);
    rmSync(target, { recursive: true, force: true });
    const status = str(envelope?.status);
    const error = envelope?.error;
    if (status === 'succeeded') {
      this.steps.set(step, { status, output: envelope!.output ?? null });
      if (existsSync(workspace.workFiles))
        cpSync(workspace.workFiles, target, { recursive: true });
    } else if (status === 'skipped')
      this.steps.set(step, {
        status,
        reason: str(envelope!.reason) ?? 'skipped by the handler',
      });
    else
      this.steps.set(step, {
        status: 'failed',
        reason:
          (error && typeof error === 'object' && !Array.isArray(error)
            ? str(error.message)
            : undefined) ?? 'no result',
      });
  }

  recordSkipped(step: string, reason: string, workspace: Workspace): void {
    rmSync(workspace.poolFilesFor(step), { recursive: true, force: true });
    this.steps.set(step, { status: 'skipped', reason });
  }
}
