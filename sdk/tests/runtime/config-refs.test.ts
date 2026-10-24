// Config references in library-step definitions (`host: TARGET.beta.host`): recorded in the manifest,
// delivered to the step automatically, resolved from ctx.config when the step runs. Plus select, the target deploys,
// registryTag and npmDistTag.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { describe, expect, test } from "bun:test";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { config, deployTarget, helmTarget, HelmRelease, model, npmDistTag, pipeline, registryTag, secret, secretRef, step, steps, z, type Ctx, type FileRef } from "../../src/index.ts";
import { pipelineData } from "../../src/builder/pipeline.ts";
import { findStep } from "../../src/harness/main.ts";
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
const describeOf = (p: unknown, handlers: string[] = []) => describePipeline(p, { handlerNames: handlers, handlerHashes: Object.fromEntries(handlers.map((h) => [h, "sha256:" + "b".repeat(64)])), harness });
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
    // Referencing only publish.repository needs no secret; publish.auth names REGISTRY_PASSWORD.
    expect(problems(base().stage(steps.imageBuild({ name: "build", description: "", repository: PUBLISH.repository, push: true })))).toEqual([]);
    const noSecret = steps.imageBuild({ name: "build", description: "", repository: PUBLISH.repository, push: true, registryAuth: PUBLISH.auth });
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

describe("target deploys and select", () => {
  const server = { repository: "registry:5000/app", tag: "2026.10.24.7", digest: "sha256:" + "ab".repeat(32) };
  const harnessImage = { repository: "registry:5000/harness", tag: "src-0123456789ab", digest: "sha256:" + "cd".repeat(32) };
  const AUTH = config("auth", "Auth.", z.discriminatedUnion("mode", [z.object({ mode: z.literal("none") }), z.object({ mode: z.literal("dex"), issuer: z.string() })]));
  const opts = (target: any) => ({
    target, chart: "helm", inlineValues: [{ auth: AUTH }, { extra: { ns: target.namespace } }],
    images: { server: "image" }, imageRefs: { harness: "harness.image" }, healthPath: "/api/health",
  }) as const;
  const builds = () => [
    steps.imageBuild({ name: "server", description: "", repository: "registry:5000/app", push: true }),
    steps.imageBuild({ name: "harness", description: "", repository: "registry:5000/harness", push: true, contentTag: true }),
  ] as const;
  const selectPipeline = () => base()
    .stage(...builds())
    .select(TARGET.prod, {
      ssh: (p, prod) => p.stage([steps.helmDeployOverSsh({ name: "deploy-prod", description: "", sshKey: KEY, ...opts(prod) }), (from) => ({ source: from.source, server: from.server, harness: from.harness })]),
      inCluster: (p, prod) => p.stage([steps.helmDeployInCluster({ name: "deploy-prod", description: "", ...opts(prod) }), (from) => ({ source: from.source, server: from.server, harness: from.harness })]),
    });
  const fakes = () => {
    const tree = tmp("helm-deploy-tree");
    mkdirSync(join(tree, "helm"));
    const calls: (string[] | { cwd?: string })[] = [];
    const ctx: Partial<Ctx> = {
      files: { fetch: async (_ref: FileRef) => tree },
      exec: async (argv, o) => void calls.push([...argv], { cwd: o?.cwd }),
      sh: Object.assign(async (cmd: string | { script: string }) => void calls.push([cmd as string]), { text: async () => "" }),
      http: { expectOk: async (url: string) => (calls.push(["GET", url]), new Response("ok")) } as unknown as Ctx["http"],
      retry: async (_o, fn) => fn(1),
    };
    return { tree, calls, ctx };
  };
  const stepsOf = (m: any, value: string) => m.stages[2].cases[value][0].steps[0];

  test("select is recorded with every case; the same step name in exclusive cases; each step declares only what it uses", () => {
    const m = describeOf(selectPipeline()) as any;
    expect(Manifest.safeParse(m).success).toBe(true);
    expect(m.stages[2]).toMatchObject({ select: "target.prod", discriminator: "kind" });
    expect(Object.keys(m.stages[2].cases).sort()).toEqual(["inCluster", "ssh"]);
    const ssh = stepsOf(m, "ssh");
    const inCluster = stepsOf(m, "inCluster");
    expect(ssh).toMatchObject({ name: "deploy-prod", kind: "helmDeployOverSsh", image: "alpine:3.22", secrets: ["SSH_PRIVATE_KEY"] });
    expect("clusterAccess" in ssh).toBe(false);
    expect(inCluster).toMatchObject({ name: "deploy-prod", kind: "helmDeployInCluster", image: "alpine/helm:3.16.2", secrets: [], clusterAccess: true });
    expect(ssh.config.target).toEqual({ $config: "target.prod", $case: "target.prod.kind=ssh" });
    expect(inCluster.config.inlineValues).toEqual([{ auth: { $config: "auth" } }, { extra: { ns: { $config: "target.prod.namespace", $case: "target.prod.kind=inCluster" } } }]);
    expect(inCluster.inputTypes.harness).toEqual({ model: "PushedImage", optional: true });
    expect(m.config.map((c: any) => c.name)).toEqual(["auth", "target"]);
  });

  test("describe: duplicate names outside exclusive cases, narrowed references outside their case, missing cases", () => {
    const deploy = (name: string) => steps.helmDeployInCluster({ name: name as "x", description: "", ...opts(TARGET.beta as any) });
    expect(problems(selectPipeline().stage([deploy("deploy-prod"), (from: any) => ({ source: from.source, server: from.server, harness: from.harness })] as any))).toContain("duplicate step name 'deploy-prod'");
    let leaked: unknown;
    const p = base().stage(...builds())
      .select(TARGET.prod, {
        ssh: (q, prod) => ((leaked = prod), q),
        inCluster: (q) => q,
      });
    const after = (p as any).stage([steps.helmDeployInCluster({ name: "late", description: "", ...opts(leaked) }), (from: any) => ({ source: from.source, server: from.server, harness: from.harness })]);
    expect(problems(after).some((x) => x.includes("is narrowed to the select case 'target.prod.kind=ssh' and can only be used inside it"))).toBe(true);
    const missing = base().select(TARGET.prod as any, { ssh: (q: any) => q } as any);
    expect(problems(missing)).toContain("select 'target.prod': no case for kind 'inCluster'");
  });

  test("a step every case has stays available after the select", () => {
    const check = step({ name: "check", description: "", image: "alpine", input: { release: HelmRelease } });
    const m = describeOf(selectPipeline().stage(check), ["check"]) as any;
    expect(m.stages[3].steps[0].input).toEqual({ release: { ref: "deploy-prod" } });
  });

  test("the harness runs the variant of the selected case (OLVE_CASES)", () => {
    const data = pipelineData(selectPipeline());
    expect(findStep(data, "server", [])).toMatchObject({ kind: "imageBuild" });
    expect(findStep(data, "deploy-prod", ["target.prod.kind=inCluster"])).toMatchObject({ kind: "helmDeployInCluster" });
    expect(findStep(data, "deploy-prod", ["target.prod.kind=ssh"])).toMatchObject({ kind: "helmDeployOverSsh" });
    expect(findStep(data, "deploy-prod", [])).toContain("OLVE_CASES must name the selected one");
  });

  test("ssh target: pulls by digest on the host, helm with the target's values, generated values and image flags", async () => {
    const f = fakes();
    const deploy = steps.helmDeployOverSsh({ name: "deploy-beta", description: "", sshKey: KEY, ...opts(TARGET.beta) });
    const r = await testHandler(deploy as any, { source, server, harness: undefined }, {
      ctx: f.ctx, secrets: { SSH_PRIVATE_KEY: "k" },
      config: { target: { beta: { kind: "ssh", host: "u@h", namespace: "apps-beta", values: ["values-beta.yaml"], set: { "slo.enabled": "false" }, url: "https://beta.example/" }, prod: { kind: "ssh", host: "u@h", namespace: "apps" } }, auth: { mode: "none" } },
    });
    expect(r).toMatchObject({ status: "succeeded", output: { release: "app", namespace: "apps-beta", image: `registry:5000/app:2026.10.24.7@${server.digest}`, url: "https://beta.example" } });
    const flat = f.calls.filter(Array.isArray).map((c) => (c as string[]).join(" "));
    expect(flat.some((c) => c.includes(`nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io pull -q registry:5000/app@${server.digest}`))).toBe(true);
    const helm = flat.find((c) => c.includes("helm upgrade --install app ."))!;
    expect(helm).toContain("-n apps-beta -f values-beta.yaml -f olve-values-0.json -f olve-values-1.json --set slo.enabled=false");
    expect(helm).toContain(`--set-string image.digest=${server.digest}`);
    expect(helm).not.toContain("harness.image");
    expect(JSON.parse(readFileSync(join(f.tree, "helm", "olve-values-0.json"), "utf8"))).toEqual({ auth: { mode: "none" } });
    expect(JSON.parse(readFileSync(join(f.tree, "helm", "olve-values-1.json"), "utf8"))).toEqual({ extra: { ns: "apps-beta" } });
    expect(flat.at(-1)).toBe("GET https://beta.example/api/health");
    const wrong = await testHandler(deploy as any, { source, server }, { ctx: fakes().ctx, secrets: { SSH_PRIVATE_KEY: "k" }, config: { target: { beta: { kind: "inCluster", namespace: "x" }, prod: { kind: "inCluster", namespace: "y" } }, auth: { mode: "none" } } });
    if (wrong.status !== "failed") throw new Error("expected failure");
    expect(wrong.error.message).toContain("the target is kind 'inCluster', not 'ssh'");
  });

  test("inCluster target: needs the ServiceAccount token, then helm --wait in the step pod", async () => {
    const deploy = steps.helmDeployInCluster({ name: "deploy-beta", description: "", ...opts(TARGET.beta) });
    const config = { target: { beta: { kind: "inCluster", namespace: "selfdeploy-beta" }, prod: { kind: "inCluster", namespace: "selfdeploy-prod" } }, auth: { mode: "none" } };
    if (!existsSync(SERVICE_ACCOUNT_TOKEN)) {
      const r = await testHandler(deploy as any, { source, server, harness: harnessImage }, { ctx: fakes().ctx, config });
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
    const r = await testHandler(deploy as any, { source, server, harness: harnessImage }, { ctx: f.ctx, config });
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

describe("steps.composeDeploy and deployTarget", () => {
  const TOKEN = secret("DEPLOYER_TOKEN", "deployer token");
  const T = config("target", "Where to deploy.", z.object({
    beta: deployTarget({ composeToken: TOKEN, optional: true }),
    prod: deployTarget({ composeToken: TOKEN, release: "app" }),
  }));
  const image = { repository: "registry:5000/app", tag: "2026.10.24.9", digest: "sha256:" + "ef".repeat(32) };
  const deploy = steps.composeDeploy({ name: "deploy-prod", description: "", target: T.prod as any, token: TOKEN });
  const docConfig = { target: { beta: { kind: "none" }, prod: { kind: "compose", deployerUrl: "http://deployer:8080/", service: "pipelines", token: { secret: "DEPLOYER_TOKEN" } } } };

  test("contract: only the token, no cluster access; the schema has compose, and none only where optional", () => {
    expect(deploy).toMatchObject({ kind: "composeDeploy", image: "alpine:3.22", secrets: [TOKEN] });
    expect("clusterAccess" in deploy).toBe(false);
    expect(T.schema.safeParse({ beta: { kind: "none" }, prod: docConfig.target.prod }).success).toBe(true);
    expect(T.schema.safeParse({ beta: { kind: "none" }, prod: { kind: "none" } }).success).toBe(false);
  });

  test("POSTs the image by digest with the token and reports the deployer's answer", async () => {
    const seen: { url: string; init?: RequestInit }[] = [];
    let answer: { status: number; body: unknown } = { status: 200, body: { status: "deployed", service: "pipelines", image: "x", previousImage: "old", log: ["pulled", "healthy"] } };
    const ctx = { http: { fetch: async (url: string, init?: RequestInit) => (seen.push({ url, init }), Response.json(answer.body, { status: answer.status })) } as unknown as Ctx["http"] };
    const r = await testHandler(deploy as any, { image }, { ctx, config: docConfig, secrets: { DEPLOYER_TOKEN: "tok" } });
    expect(r).toMatchObject({ status: "succeeded", output: { release: "pipelines", namespace: "compose", image: `registry:5000/app@${image.digest}` } });
    expect(seen[0]!.url).toBe("http://deployer:8080/v1/deploy");
    expect(new Headers(seen[0]!.init?.headers).get("authorization")).toBe("Bearer tok");
    expect(JSON.parse(String(seen[0]!.init?.body))).toEqual({ service: "pipelines", image: `registry:5000/app@${image.digest}` });
    answer = { status: 502, body: { status: "rolled-back", message: "health timed out" } };
    const failed = await testHandler(deploy as any, { image }, { ctx, config: docConfig, secrets: { DEPLOYER_TOKEN: "tok" } });
    if (failed.status !== "failed") throw new Error("expected failure");
    expect(failed.error.message).toContain("the deployer answered HTTP 502: rolled-back: health timed out");
  });
});
