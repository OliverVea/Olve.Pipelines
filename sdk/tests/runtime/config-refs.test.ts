// Config references in library-step definitions (`host: TARGET.beta.host`): recorded in the manifest,
// delivered to the step automatically, resolved from ctx.config when the step runs. Plus helmDeploy,
// registryTag and npmDistTag.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, test } from "bun:test";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { config, helmTarget, model, npmDistTag, pipeline, registryTag, secret, secretRef, steps, z, type Ctx, type FileRef } from "../../src/index.ts";
import { configRefPath, isConfigRef } from "../../src/core/config.ts";
import { configRefsIn, recordConfigRefs, resolveConfigRefs } from "../../src/core/config-refs.ts";
import { describePipeline, DescribeError } from "../../src/describe/describe.ts";
import { Manifest } from "../../src/describe/manifest-schema.ts";
import { SERVICE_ACCOUNT_TOKEN } from "../../src/library/helm-deploy.ts";
import { testFileRef, testHandler } from "../../src/testing/index.ts";
import { SHA, tmp } from "./helpers.ts";

const harness = { bun: "1.4.3", sdk: "0.0.0", sdkHash: "sha256:" + "a".repeat(64) };
const KEY = secret("SSH_PRIVATE_KEY", "ssh key");
const REG = secret("REGISTRY_PASSWORD", "registry password");
const TARGET = config("target", "Where to deploy.", z.object({ beta: helmTarget({ release: "app" }), prod: helmTarget({ release: "app" }) }));
const PUBLISH = config("publish", "Where images go.", z.object({
  repository: z.string(),
  push: z.boolean().default(true),
  insecure: z.boolean().default(false),
  auth: z.object({ username: z.string(), password: secretRef(REG) }).optional(),
}));
const NAME = config("name", "A plain string entry.", z.string());

const Req = model("Req", z.object({ sha: z.string() }));
const source = { repo: "o/r", sha: SHA, tree: testFileRef({ step: "source", path: "tree/" }) };
const describeOf = (p: unknown) => describePipeline(p, { handlerNames: [], handlerHashes: {}, harness });
const problems = (p: unknown): string[] => {
  try {
    describeOf(p);
    return [];
  } catch (e) {
    if (e instanceof DescribeError) return [...e.problems];
    throw e;
  }
};
const checkout = steps.gitHubSource({ name: "source", description: "" });
const base = () => pipeline({ name: "t", input: Req }).stage([checkout, (from) => ({ repo: "o/r", sha: from.input.sha })]);

describe("config references", () => {
  test("entries and fields are references; the entry keeps its own properties", () => {
    expect(TARGET.name).toBe("target");
    expect(TARGET.kind).toBe("config");
    expect(isConfigRef(TARGET)).toBe(true);
    expect(configRefPath(TARGET.beta.namespace)).toBe("target.beta.namespace");
    expect(configRefPath(NAME.$)).toBe("name");
    expect((TARGET as any).then).toBeUndefined();
    expect(configRefsIn({ a: [TARGET.prod], b: { c: PUBLISH.repository } }).map((e) => e.name)).toEqual(["target", "publish"]);
  });

  test("recorded as {$config} and resolved from config; literal '$' keys are reserved", () => {
    expect(recordConfigRefs({ host: TARGET.beta, set: { a: "x" }, list: [PUBLISH.repository] })).toEqual({
      host: { $config: "target.beta" }, set: { a: "x" }, list: [{ $config: "publish.repository" }],
    });
    expect(() => recordConfigRefs({ $config: "x" })).toThrow("reserved");
    const cfg = { target: { beta: { kind: "ssh", host: "u@h" } }, publish: { repository: "r" } };
    expect(resolveConfigRefs({ h: TARGET.beta.namespace, k: (TARGET.beta as any).host, r: [PUBLISH.repository] }, cfg) as unknown).toEqual({ h: undefined, k: "u@h", r: ["r"] });
    expect(() => resolveConfigRefs(NAME.$, {})).toThrow("was not delivered");
  });

  test("a library step receives the entries it references; the manifest records the reference", () => {
    const build = steps.imageBuild({
      name: "build", description: "", repository: PUBLISH.repository, push: true, registryMap: {},
      insecureRegistry: PUBLISH.insecure, registryAuth: PUBLISH.auth, secrets: [REG], enabled: PUBLISH.push,
    });
    expect((build.config as readonly any[]).map((e) => e.name)).toEqual(["publish"]);
    expect(build.secrets.map((s) => s.name)).toEqual(["REGISTRY_PASSWORD"]);
    const m = describeOf(base().stage(build)) as any;
    expect(Manifest.safeParse(m).success).toBe(true);
    const s = m.stages[1].steps[0];
    expect(s.configEntries).toEqual(["publish"]);
    expect(s.config).toEqual({
      repository: { $config: "publish.repository" }, push: true, insecureRegistry: { $config: "publish.insecure" },
      registryAuth: { $config: "publish.auth" }, enabled: { $config: "publish.push" },
    });
    expect(m.config.map((c: any) => c.name)).toEqual(["publish"]);
  });

  test("describe rejects a reference to a field the entry doesn't have, and a missing secret declaration", () => {
    const bad = steps.helmDeployOverSsh({ name: "deploy", description: "", host: (TARGET.beta as any).nope, sshKey: KEY, release: "a", namespace: "n", chart: "helm" });
    expect(problems(base().stage(steps.imageBuild({ name: "build", description: "", repository: "x" })).stage(bad))).toEqual([
      "step 'deploy': config reference 'target.beta.nope': config 'target' has no field 'beta.nope'",
    ]);
    const noSecret = steps.imageBuild({ name: "build", description: "", repository: PUBLISH.repository, push: true });
    expect(problems(base().stage(noSecret))).toEqual([
      "step 'build': config 'publish' references secret 'REGISTRY_PASSWORD' (secretRef), so the step must declare it in secrets",
    ]);
  });

  test("imageBuild resolves its references when it runs; enabled: false skips", async () => {
    const tree = tmp("cfg-ref-tree");
    writeFileSync(join(tree, "Dockerfile"), "FROM scratch\n");
    const calls: string[][] = [];
    const digest = "sha256:" + "cd".repeat(32);
    const ctx = {
      files: { fetch: async () => tree },
      exec: async (argv: readonly string[]) => {
        calls.push([...argv]);
        if (argv[0] === "/kaniko/executor") writeFileSync(argv.find((a) => a.startsWith("--digest-file="))!.slice(14), digest);
      },
    };
    const step = steps.imageBuild({ name: "build", description: "", repository: PUBLISH.repository, push: true, registryMap: {}, insecureRegistry: PUBLISH.insecure, enabled: PUBLISH.push, registryAuth: PUBLISH.auth, secrets: [REG] });
    const dockerConfig = tmp("cfg-ref-docker");
    process.env.DOCKER_CONFIG = dockerConfig;
    try {
      const r = await testHandler(step, { source }, {
        ctx, secrets: { REGISTRY_PASSWORD: "pw" },
        config: { publish: { repository: "registry:5000/app", insecure: true, auth: { username: "local", password: { secret: "REGISTRY_PASSWORD" } } } },
      });
      expect(r).toMatchObject({ status: "succeeded", output: { repository: "registry:5000/app", digest } });
      const argv = calls.find((c) => c[0] === "/kaniko/executor")!;
      expect(argv.filter((a) => a.startsWith("--destination=")).map((a) => a.split(":")[0])).toEqual(["--destination=registry"]);
      expect(argv).toContain("--insecure-registry=registry:5000");
      expect(JSON.parse(readFileSync(join(dockerConfig, "config.json"), "utf8")).auths["registry:5000"]).toBeDefined();

      const off = await testHandler(step, { source }, { ctx, config: { publish: { repository: "registry:5000/app", push: false } } });
      expect(off).toMatchObject({ status: "skipped", reason: "publishing registry:5000/app is turned off" });
      const bad = await testHandler(step, { source }, { ctx, config: { publish: { repository: "app" } } });
      if (bad.status !== "failed") throw new Error("expected failure");
      expect(bad.error.message).toContain("push needs a repository with a registry host");
    } finally {
      delete process.env.DOCKER_CONFIG;
    }
  });
});

describe("steps.helmDeploy", () => {
  const server = { repository: "registry:5000/app", tag: "2026.10.24.7", digest: "sha256:" + "ab".repeat(32) };
  const harnessImage = { repository: "registry:5000/harness", tag: "src-0123456789ab", digest: "sha256:" + "cd".repeat(32) };
  const AUTH = config("auth", "Auth.", z.discriminatedUnion("mode", [z.object({ mode: z.literal("none") }), z.object({ mode: z.literal("dex"), issuer: z.string() })]));
  const deploy = steps.helmDeploy({
    name: "deploy-beta", description: "", target: TARGET.beta, sshKey: KEY, chart: "helm",
    inlineValues: [{ auth: AUTH }, { extra: { ns: TARGET.beta.namespace } }],
    images: { server: "image" }, imageRefs: { harness: "harness.image" }, healthPath: "/api/health",
  });
  const fakes = () => {
    const tree = tmp("helm-deploy-tree");
    mkdirSync(join(tree, "helm"));
    const calls: (string[] | { cwd?: string })[] = [];
    const ctx: Partial<Ctx> = {
      files: { fetch: async (_ref: FileRef) => tree },
      exec: async (argv, opts) => void calls.push([...argv], { cwd: opts?.cwd }),
      sh: Object.assign(async (cmd: string | { script: string }) => void calls.push([cmd as string]), { text: async () => "" }),
      http: { expectOk: async (url: string) => (calls.push(["GET", url]), new Response("ok")) } as unknown as Ctx["http"],
      retry: async (_o, fn) => fn(1),
    };
    return { tree, calls, ctx };
  };

  test("contract: registry images, optional imageRefs, cluster access, the target's entry", () => {
    expect(deploy).toMatchObject({ kind: "helmDeploy", image: "alpine/helm:3.16.2", clusterAccess: true, secrets: [KEY] });
    expect((deploy.config as readonly any[]).map((e) => e.name)).toEqual(["target", "auth"]);
    const m = describeOf(base()
      .stage(steps.imageBuild({ name: "server", description: "", repository: "registry:5000/app", push: true }),
        steps.imageBuild({ name: "harness", description: "", repository: "registry:5000/harness", push: true, contentTag: true }))
      .stage([deploy, (from) => ({ source: from.source, server: from.server, harness: from.harness })])) as any;
    expect(Manifest.safeParse(m).success).toBe(true);
    const s = m.stages[2].steps[0];
    expect(s).toMatchObject({ kind: "helmDeploy", clusterAccess: true, configEntries: ["auth", "target"] });
    expect(s.inputTypes.harness).toEqual({ model: "PushedImage", optional: true });
    expect(s.config.target).toEqual({ $config: "target.beta" });
    expect(s.config.inlineValues).toEqual([{ auth: { $config: "auth" } }, { extra: { ns: { $config: "target.beta.namespace" } } }]);
  });

  test("ssh target: pulls by digest on the host, helm with the target's values, generated values and image flags", async () => {
    const f = fakes();
    const r = await testHandler(deploy, { source, server, harness: undefined } as any, {
      ctx: f.ctx, secrets: { SSH_PRIVATE_KEY: "k" },
      config: { target: { beta: { kind: "ssh", host: "u@h", namespace: "apps-beta", values: ["values-beta.yaml"], set: { "slo.enabled": "false" } }, prod: { kind: "ssh", host: "u@h", namespace: "apps" } }, auth: { mode: "none" } },
    });
    expect(r).toMatchObject({ status: "succeeded", output: { release: "app", namespace: "apps-beta", image: `registry:5000/app:2026.10.24.7@${server.digest}` } });
    const flat = f.calls.filter(Array.isArray).map((c) => (c as string[]).join(" "));
    expect(flat.some((c) => c.includes(`nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io pull -q registry:5000/app@${server.digest}`))).toBe(true);
    const helm = flat.find((c) => c.includes("helm upgrade --install app ."))!;
    expect(helm).toContain("-n apps-beta -f values-beta.yaml -f olve-values-0.json -f olve-values-1.json --set slo.enabled=false");
    expect(helm).toContain(`--set-string image.digest=${server.digest}`);
    expect(helm).not.toContain("harness.image");
    expect(JSON.parse(readFileSync(join(f.tree, "helm", "olve-values-0.json"), "utf8"))).toEqual({ auth: { mode: "none" } });
    expect(JSON.parse(readFileSync(join(f.tree, "helm", "olve-values-1.json"), "utf8"))).toEqual({ extra: { ns: "apps-beta" } });
    expect(flat.at(-1)).toBe("GET http://app.apps-beta.svc.cluster.local/api/health");
  });

  test("inCluster target: needs the ServiceAccount token, then helm --wait in the step pod", async () => {
    const config = { target: { beta: { kind: "inCluster", namespace: "selfdeploy-beta" }, prod: { kind: "inCluster", namespace: "selfdeploy-prod" } }, auth: { mode: "none" } };
    const tokenExists = existsSync(SERVICE_ACCOUNT_TOKEN);
    if (!tokenExists) {
      const f = fakes();
      const r = await testHandler(deploy, { source, server, harness: harnessImage }, { ctx: f.ctx, config });
      if (r.status !== "failed") throw new Error("expected failure");
      expect(r.error.message).toContain("no ServiceAccount token in the step pod");
      try {
        mkdirSync(dirname(SERVICE_ACCOUNT_TOKEN), { recursive: true });
        writeFileSync(SERVICE_ACCOUNT_TOKEN, "token");
      } catch {
        return; // not root: the success path is covered where the token path is writable (the Linux test container)
      }
    }
    const f = fakes();
    const r = await testHandler(deploy, { source, server, harness: harnessImage }, { ctx: f.ctx, config });
    expect(r).toMatchObject({ status: "succeeded", output: { namespace: "selfdeploy-beta" } });
    const helm = f.calls.find((c) => Array.isArray(c) && c[0] === "helm") as string[];
    expect(helm.slice(0, 6)).toEqual(["helm", "upgrade", "--install", "app", ".", "-n"]);
    expect(helm).toContain("--wait");
    expect(helm.join(" ")).toContain(`--set-string harness.image.digest=${harnessImage.digest}`);
    expect(f.calls[f.calls.indexOf(helm) + 1]).toEqual({ cwd: join(f.tree, "helm") });
  });
});

describe("registryTag and npmDistTag", () => {
  test("registryTag re-PUTs the manifest bytes under the tag", async () => {
    const digest = "sha256:" + "ef".repeat(32);
    const body = '{"schemaVersion":2}';
    const requests: { method: string; url: string; type?: string | null; body?: string }[] = [];
    const http = {
      fetch: async (url: string, init?: RequestInit) => {
        const h = new Headers(init?.headers);
        requests.push({ method: init?.method ?? "GET", url, type: h.get("content-type"), body: init?.body ? new TextDecoder().decode(init.body as Uint8Array) : undefined });
        if (init?.method === "PUT") return new Response(null, { status: 201, headers: { "Docker-Content-Digest": digest } });
        return new Response(body, { headers: { "Content-Type": "application/vnd.oci.image.manifest.v1+json" } });
      },
    } as unknown as Ctx["http"];
    await registryTag(http, "registry:5000/app", digest, "next", { insecure: true });
    expect(requests).toEqual([
      { method: "GET", url: `http://registry:5000/v2/app/manifests/${digest}`, type: null, body: undefined },
      { method: "PUT", url: "http://registry:5000/v2/app/manifests/next", type: "application/vnd.oci.image.manifest.v1+json", body },
    ]);
  });

  test("npmDistTag PUTs the version to the dist-tag", async () => {
    const seen: { url: string; init?: RequestInit }[] = [];
    const ctx = { http: { fetch: async (url: string, init?: RequestInit) => (seen.push({ url, init }), new Response(null, { status: 200 })) }, log: () => {} } as unknown as Ctx;
    await npmDistTag(ctx, { name: "@olivervea/pipelines", version: "2.0.0", tag: "latest", token: "t" });
    expect(seen[0]!.url).toBe("https://registry.npmjs.org/-/package/@olivervea%2fpipelines/dist-tags/latest");
    expect(seen[0]!.init).toMatchObject({ method: "PUT", body: '"2.0.0"' });
  });
});
