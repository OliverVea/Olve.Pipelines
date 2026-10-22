import { describe, expect, test } from "bun:test";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { Ctx } from "@olivervea/pipelines";
import { testFileRef, testHandler } from "@olivervea/pipelines/testing";
import publishSdk from "../handlers/publish-sdk";

const source = { repo: "OliverVea/Olve.Pipelines", sha: "a".repeat(40), tree: testFileRef({ step: "source", path: "tree/" }) };

function fakes(publish: { exitCode: number; output: string }) {
  const tree = mkdtempSync(join(tmpdir(), "publish-sdk-"));
  mkdirSync(join(tree, "sdk"));
  writeFileSync(join(tree, "sdk/package.json"), JSON.stringify({ name: "@olivervea/pipelines", version: "2.0.0-beta.9" }));
  const ctx: Partial<Ctx<"NPM_TOKEN">> = {
    files: { fetch: async () => tree },
    http: { fetch: async () => new Response("", { status: 404 }) } as unknown as Ctx["http"],
    exec: async () => {},
    sh: Object.assign(
      async (cmd: string | { script: string }) => {
        writeFileSync(String(cmd).match(/> (\S+) 2>&1/)![1]!, publish.output);
        if (publish.exitCode !== 0) throw Object.assign(new Error(`\`bun publish\` failed with exit code ${publish.exitCode}`), { exitCode: publish.exitCode });
      },
      { text: async () => "" },
    ),
  };
  return ctx;
}

describe("publish-sdk", () => {
  process.env.NPM_TOKEN = "token";

  test("publishes a version npm doesn't have", async () => {
    const r = await testHandler(publishSdk, { source }, { ctx: fakes({ exitCode: 0, output: "+ @olivervea/pipelines@2.0.0-beta.9\n" }) });
    expect(r).toMatchObject({ status: "succeeded", output: { name: "@olivervea/pipelines", version: "2.0.0-beta.9" } });
  });

  test("a version another run published after the lookup is a skip", async () => {
    const output = "error: You cannot publish over the previously published versions: 2.0.0-beta.9\n";
    const r = await testHandler(publishSdk, { source }, { ctx: fakes({ exitCode: 1, output }) });
    expect(r).toMatchObject({ status: "skipped", reason: "@olivervea/pipelines@2.0.0-beta.9 was published by another run" });
  });

  test("any other publish error fails the step", async () => {
    const r = await testHandler(publishSdk, { source }, { ctx: fakes({ exitCode: 1, output: "error: 403 Forbidden\n" }) });
    expect(r).toMatchObject({ status: "failed", error: { kind: "handler" } });
  });
});
