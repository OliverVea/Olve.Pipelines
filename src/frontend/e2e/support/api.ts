import {
  bindingsCreate,
  bindingsReconcile,
  bindingsSetPolling,
  instanceFrontendConfig,
  runsStart,
  secretsSet,
} from '@olve/api';
import { createClient, createConfig } from '@olve/api/client';
import { expect } from '@playwright/test';

export type TestPipeline = { id: string; name: string; repo: string };

/** Names everything a test creates: the host's sweeper removes what carries this prefix once it is old. */
export function newName(hint: string): string {
  return `e2e-${hint}-${crypto.randomUUID().replaceAll('-', '').slice(0, 12)}`;
}

export function repoOf(name: string): string {
  return `e2e/${name}`;
}

/** The host's API, for what a test sets up rather than does in the browser. */
export class HostApi {
  private readonly client;

  constructor(apiUrl: string) {
    this.client = createClient(createConfig({ baseUrl: apiUrl }));
  }

  async frontendConfig() {
    const { data } = await instanceFrontendConfig({
      client: this.client,
      throwOnError: true,
    });
    return data;
  }

  /** Binds a repo with polling off, so the test decides when it syncs, and returns the pipeline's id. */
  async bind(repo: string): Promise<string> {
    const client = this.client;
    const { data: binding } = await bindingsCreate({
      client,
      throwOnError: true,
      body: { repo },
    });
    const pipelineId = binding.pipelineId;
    await bindingsSetPolling({
      client,
      throwOnError: true,
      path: { pipelineId },
      body: { polls: false },
    });
    return pipelineId;
  }

  /** A new pipeline holding the fixture, with its first version active. */
  async newPipeline(hint: string): Promise<TestPipeline> {
    const name = newName(hint);
    const repo = repoOf(name);
    const id = await this.bind(repo);
    await bindingsReconcile({
      client: this.client,
      throwOnError: true,
      path: { pipelineId: id },
      query: { wait: true },
    });
    return { id, name, repo };
  }

  async setSecret(pipeline: TestPipeline, name: string, value: string) {
    await secretsSet({
      client: this.client,
      throwOnError: true,
      path: { pipelineId: pipeline.id, name },
      body: { value },
    });
  }

  /** Starts a run and returns its number. */
  async startRun(pipeline: TestPipeline, input: Record<string, unknown> = {}) {
    const { data } = await runsStart({
      client: this.client,
      throwOnError: true,
      path: { pipelineId: pipeline.id },
      body: input,
    });
    expect(data.run, `the run did not start: ${data.outcome}`).toBeTruthy();
    return data.run!.number;
  }
}
