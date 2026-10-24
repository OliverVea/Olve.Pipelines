import { describe, expect, test } from "bun:test";
import type { Ctx } from "@olivervea/pipelines";
import { testFileRef, testHandler } from "@olivervea/pipelines/testing";
import publishSdk from "../handlers/publish-sdk";

const pkg = { name: "@olivervea/pipelines", version: "2.0.0-beta.9", tarball: testFileRef({ step: "sdk", path: "package.tgz" }) };
const publish = (npm: { enabled: boolean }) => ({ publish: { server: { repository: "r/s" }, harness: { repository: "r/h" }, npm } });

function fakes(result: { exitCode: number; output: string }) {
  const commands: string[] = [];
  const ctx = {
    files: { fetch: async () => "/tmp/package.tgz" },
    http: { fetch: async () => new Response("", { status: 404 }) } as unknown as Ctx["http"],
    sh: Object.assign(
      async (cmd: string | { script: string }) => {
        commands.push(String(cmd));
        await Bun.write(String(cmd).match(/> (\S+) 2>&1/)![1]!, result.output);
        if (result.exitCode !== 0) throw Object.assign(new Error(`\`bun publish\` failed with exit code ${result.exitCode}`), { exitCode: result.exitCode });
      },
      { text: async () => "" },
    ),
  };
  return { ctx, commands };
}

describe("publish-sdk", () => {
  process.env.NPM_TOKEN = "token";

  test("publishes the packed tarball under the prerelease tag", async () => {
    const f = fakes({ exitCode: 0, output: "+ @olivervea/pipelines@2.0.0-beta.9\n" });
    const r = await testHandler(publishSdk, { pkg }, { ctx: f.ctx, config: publish({ enabled: true }) });
    expect(r).toMatchObject({ status: "succeeded", output: { name: "@olivervea/pipelines", version: "2.0.0-beta.9" } });
    expect(f.commands[0]).toMatch(/^bun publish \S+\.tgz --tag next --access public > /);
  });

  test("npm publishing turned off is a skip, without asking npm", async () => {
    const f = fakes({ exitCode: 0, output: "" });
    const r = await testHandler(publishSdk, { pkg }, { ctx: { ...f.ctx, http: undefined }, config: publish({ enabled: false }) });
    expect(r).toMatchObject({ status: "skipped", reason: "npm publishing is turned off (publish.npm.enabled): @olivervea/pipelines@2.0.0-beta.9 not published" });
    expect(f.commands).toEqual([]);
  });

  test("a version another run published after the lookup is a skip", async () => {
    const output = "error: You cannot publish over the previously published versions: 2.0.0-beta.9\n";
    const r = await testHandler(publishSdk, { pkg }, { ctx: fakes({ exitCode: 1, output }).ctx, config: publish({ enabled: true }) });
    expect(r).toMatchObject({ status: "skipped", reason: "@olivervea/pipelines@2.0.0-beta.9 was published by another run" });
  });

  test("any other publish error fails the step", async () => {
    const r = await testHandler(publishSdk, { pkg }, { ctx: fakes({ exitCode: 1, output: "error: 403 Forbidden\n" }).ctx, config: publish({ enabled: true }) });
    expect(r).toMatchObject({ status: "failed", error: { kind: "handler" } });
  });
});
