// Library steps used by Olve.Pipelines' own deploy: gitHubSource, imageBuild, helmDeployOverSsh, httpHealthCheck.
// Run through testHandler with faked ctx parts; the assertions pin the exact commands they run.
import { describe, expect, test } from "bun:test";
import { mkdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { httpHealthCheck, model, pipeline, secret, steps, z, type Ctx, type FileRef } from "../../src/index.ts";
import { describePipeline } from "../../src/describe/describe.ts";
import { Manifest } from "../../src/describe/manifest-schema.ts";
import { testFileRef, testHandler } from "../../src/testing/index.ts";
import { writeTarGz } from "../../src/util/tar.ts";
import { SHA, tmp } from "./helpers.ts";

const TOKEN = secret("GITHUB_TOKEN", "token");
const KEY = secret("SSH_PRIVATE_KEY", "key");

/** ctx.retry without the waiting. */
const fastRetry: Ctx["retry"] = async (opts, fn) => {
  for (let i = 1; ; i++) {
    try {
      return await fn(i);
    } catch (e) {
      if (i >= opts.attempts) throw e;
    }
  }
};

const sourceRef = testFileRef({ step: "source", path: "tree/" });
const source = { repo: "OliverVea/Olve.Pipelines", sha: SHA, tree: sourceRef };

describe("steps.gitHubSource", () => {
  const tarball = async () => {
    const dir = tmp("gh-tarball");
    const top = `OliverVea-Olve.Pipelines-${SHA.slice(0, 7)}`;
    mkdirSync(join(dir, top, "helm"), { recursive: true });
    writeFileSync(join(dir, top, "Dockerfile"), "FROM scratch\n");
    writeFileSync(join(dir, top, "helm", "Chart.yaml"), "name: x\n");
    const out = join(dir, "repo.tar.gz");
    await writeTarGz(out, [
      { path: top, type: "dir", mode: 0o755 },
      { path: `${top}/Dockerfile`, type: "file", mode: 0o644, source: join(dir, top, "Dockerfile") },
      { path: `${top}/helm`, type: "dir", mode: 0o755 },
      { path: `${top}/helm/Chart.yaml`, type: "file", mode: 0o644, source: join(dir, top, "helm", "Chart.yaml") },
    ]);
    return readFileSync(out);
  };

  test("downloads the tarball at the sha with the token and outputs the stripped tree", async () => {
    const step = steps.gitHubSource({ name: "source", token: TOKEN });
    expect(step.secrets.map((s) => s.name)).toEqual(["GITHUB_TOKEN"]);
    expect(step.image).toBe("alpine:3.22");
    const body = await tarball();
    const calls: { url: string; headers: Record<string, string> }[] = [];
    const fetch = async (url: string, init?: RequestInit) => {
      calls.push({ url, headers: init?.headers as Record<string, string> });
      return new Response(body);
    };
    const r = await testHandler(step, { repo: "OliverVea/Olve.Pipelines", sha: SHA }, {
      secrets: { GITHUB_TOKEN: "ghp_test" },
      ctx: { http: { fetch } as unknown as Ctx["http"] },
    });
    expect(r.status).toBe("succeeded");
    expect(calls).toEqual([
      {
        url: `https://api.github.com/repos/OliverVea/Olve.Pipelines/tarball/${SHA}`,
        headers: { accept: "application/vnd.github+json", "user-agent": "olve-pipelines", authorization: "Bearer ghp_test" },
      },
    ]);
    if (r.status !== "succeeded") return;
    expect(r.output).toMatchObject({ repo: "OliverVea/Olve.Pipelines", sha: SHA, tree: { step: "source", path: "tree/" } });
    expect(readFileSync(join(r.filesDir, "tree.tar")).length).toBeGreaterThan(0);
  });

  test("the tree ref extracts to the repository root", async () => {
    const step = steps.gitHubSource({ name: "source" });
    const body = await tarball();
    let extracted = "";
    const r = await testHandler(step, { repo: "a/b", sha: SHA }, {
      ctx: { http: { fetch: async () => new Response(body) } as unknown as Ctx["http"] },
    });
    if (r.status !== "succeeded") throw new Error(JSON.stringify(r));
    // Re-run a fetch of the produced ref through a real ctx bound to the same attempt.
    const check = await testHandler(
      { ...step, builtin: { ...step.builtin, run: async (_i: unknown, ctx: Ctx) => { extracted = await ctx.files.fetch(r.output.tree); return r.output; } } } as typeof step,
      { repo: "a/b", sha: SHA },
      { dir: r.dir },
    );
    expect(check.status).toBe("succeeded");
    expect(readFileSync(join(extracted, "Dockerfile"), "utf8")).toBe("FROM scratch\n");
    expect(readFileSync(join(extracted, "helm", "Chart.yaml"), "utf8")).toBe("name: x\n");
  });

  test("no token: no authorization header and no secrets; 4xx fails at once, 5xx is retried", async () => {
    const step = steps.gitHubSource({ name: "source" });
    expect(step.secrets).toEqual([]);
    let calls = 0;
    const notFound = await testHandler(step, { repo: "a/b", sha: SHA }, {
      ctx: {
        http: { fetch: async (_u: string, init?: RequestInit) => { calls++; expect(init?.headers).not.toHaveProperty("authorization"); return new Response("nope", { status: 404 }); } } as unknown as Ctx["http"],
        retry: fastRetry,
      },
    });
    expect(notFound).toMatchObject({ status: "failed", error: { kind: "handler" } });
    if (notFound.status === "failed") expect(notFound.error.message).toContain("HTTP 404");
    expect(calls).toBe(1);

    const body = await tarball();
    const statuses = [502, 503, 200];
    const flaky = await testHandler(step, { repo: "a/b", sha: SHA }, {
      ctx: {
        http: { fetch: async () => { const s = statuses.shift()!; return s === 200 ? new Response(body) : new Response("", { status: s }); } } as unknown as Ctx["http"],
        retry: fastRetry,
      },
    });
    expect(flaky.status).toBe("succeeded");
  });

  test("rejects a malformed repo", async () => {
    const r = await testHandler(steps.gitHubSource({ name: "source" }), { repo: "not a repo", sha: SHA });
    expect(r).toMatchObject({ status: "failed", error: { kind: "input" } });
  });
});

describe("steps.imageBuild", () => {
  const fakeKaniko = (opts: { digest?: string } = {}) => {
    const tree = tmp("kaniko-tree");
    const calls: string[][] = [];
    const exec: Ctx["exec"] = async (argv) => {
      calls.push([...argv]);
      if (argv[0] === "/kaniko/executor") {
        const flag = (n: string) => argv.find((a) => a.startsWith(`--${n}=`))!.slice(n.length + 3);
        writeFileSync(flag("tar-path"), "image tar");
        if (opts.digest !== "") writeFileSync(flag("digest-file"), (opts.digest ?? "sha256:" + "ab".repeat(32)) + "\n");
      }
    };
    const fetched: FileRef[] = [];
    const files = { fetch: async (ref: FileRef) => (fetched.push(ref), tree) };
    return { tree, calls, exec, files, fetched };
  };

  test("kaniko build with layer cache: exact argv, BuiltImage output", async () => {
    const step = steps.imageBuild({ name: "build", repository: "olve-pipelines-v2", cacheRepo: "registry.ovea.pro/olve-pipelines-v2/cache", timeoutSeconds: 3600 });
    expect(step).toMatchObject({ image: "gcr.io/kaniko-project/executor:debug", timeoutSeconds: 3600, secrets: [] });
    const k = fakeKaniko();
    const r = await testHandler(step, { source }, { ctx: { exec: k.exec, files: k.files }, run: { version: "2026.10.21.412" } });
    expect(r.status).toBe("succeeded");
    expect(k.fetched).toEqual([sourceRef]);
    const out = join(r.dir, "tmp");
    const kaniko = k.calls[2]!;
    const tarPath = kaniko.find((a) => a.startsWith("--tar-path="))!.slice("--tar-path=".length);
    expect(tarPath.startsWith(out)).toBe(true);
    expect(k.calls).toEqual([
      ["mkdir", "-p", "/kaniko/build-context"],
      ["cp", "-r", `${k.tree}/.`, "/kaniko/build-context"],
      [
        "/kaniko/executor",
        "--context=/kaniko/build-context",
        "--dockerfile=/kaniko/build-context/Dockerfile",
        "--no-push",
        `--tar-path=${tarPath}`,
        `--digest-file=${tarPath.replace(/image\.tar$/, "digest")}`,
        "--destination=olve-pipelines-v2:2026.10.21.412",
        "--cache=true",
        "--cache-repo=registry.ovea.pro/olve-pipelines-v2/cache",
      ],
    ]);
    if (r.status !== "succeeded") return;
    expect(r.output).toMatchObject({
      repository: "docker.io/library/olve-pipelines-v2",
      tag: "2026.10.21.412",
      digest: "sha256:" + "ab".repeat(32),
      tar: { step: "build", path: "image.tar" },
    });
    expect(readFileSync(join(r.filesDir, "image.tar"), "utf8")).toBe("image tar");
  });

  test("no cache flags without cacheRepo; reruns get a distinct tag", async () => {
    const k = fakeKaniko();
    const r = await testHandler(steps.imageBuild({ name: "build", repository: "registry.ovea.pro/app" }), { source }, {
      ctx: { exec: k.exec, files: k.files },
      run: { version: "2026.10.21.7", attempt: 2 },
    });
    expect(r).toMatchObject({ status: "succeeded", output: { repository: "registry.ovea.pro/app", tag: "2026.10.21.7-attempt2" } });
    expect(k.calls[2]).toContain("--destination=registry.ovea.pro/app:2026.10.21.7-attempt2");
    expect(k.calls[2]!.some((a) => a.startsWith("--cache"))).toBe(false);
  });

  test("a missing digest file or an invalid tag fails the step", async () => {
    const k = fakeKaniko({ digest: "" });
    const r = await testHandler(steps.imageBuild({ name: "build", repository: "x" }), { source }, { ctx: { exec: k.exec, files: k.files } });
    expect(r).toMatchObject({ status: "failed", error: { kind: "handler" } });
    if (r.status === "failed") expect(r.error.message).toContain("did not write the image digest");
    const bad = await testHandler(steps.imageBuild({ name: "build", repository: "x" }), { source }, { ctx: { exec: fakeKaniko().exec, files: k.files }, run: { version: "1+2" } });
    if (bad.status === "failed") expect(bad.error.message).toContain("not a valid image tag");
    else throw new Error("expected failure");
  });
});

describe("steps.helmDeployOverSsh", () => {
  const def = {
    name: "deploy-beta",
    host: "oliver@bulwark-m2",
    sshKey: KEY,
    release: "olve-pipelines-v2",
    namespace: "apps-beta",
    chart: "helm",
    values: ["values-beta.yaml", "values-v2-beta.yaml"],
    set: { "slo.enabled": "false" },
    healthUrl: "http://olve-pipelines-v2.apps-beta.svc.cluster.local/api/health",
  } as const;
  const image = {
    repository: "docker.io/library/olve-pipelines-v2",
    tag: "2026.10.21.412",
    digest: "sha256:" + "ab".repeat(32),
    tar: testFileRef({ step: "build", path: "image.tar" }),
  };

  const fakes = (opts: { chart?: boolean; healthy?: number } = {}) => {
    const tree = tmp("helm-tree");
    if (opts.chart !== false) mkdirSync(join(tree, "helm"));
    const tar = join(tmp("helm-image"), "image.tar");
    writeFileSync(tar, "tar");
    const calls: (string[] | string)[] = [];
    let health = 0;
    const ctx: Partial<Ctx<"SSH_PRIVATE_KEY">> = {
      files: { fetch: async (ref: FileRef) => (ref.path === "tree/" ? tree : tar) },
      exec: async (argv) => void calls.push([...argv]),
      sh: Object.assign(async (cmd: string | { script: string }) => void calls.push(cmd as string), { text: async () => "" }),
      http: {
        expectOk: async (url: string) => {
          calls.push(`GET ${url}`);
          if (++health < (opts.healthy ?? 1)) throw new Error("HTTP 503");
          return new Response("ok");
        },
      } as unknown as Ctx["http"],
      retry: fastRetry,
    };
    return { tree, tar, calls, ctx };
  };

  test("imports the image, copies the chart, helm-upgrades, waits for rollout, checks health", async () => {
    const step = steps.helmDeployOverSsh(def);
    expect(step).toMatchObject({ image: "alpine:3.22", secrets: [KEY] });
    const f = fakes({ healthy: 3 });
    const r = await testHandler(step, { source, image }, { secrets: { SSH_PRIVATE_KEY: "-----KEY-----" }, ctx: f.ctx });
    expect(r).toMatchObject({
      status: "succeeded",
      output: { release: "olve-pipelines-v2", namespace: "apps-beta", image: "docker.io/library/olve-pipelines-v2:2026.10.21.412" },
    });

    const key = join(r.dir, "tmp", "ssh", "id");
    expect(readFileSync(key, "utf8")).toBe("-----KEY-----\n");
    expect(statSync(key).mode & 0o777).toBe(0o600);
    const o = `-i ${key} -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o LogLevel=ERROR -o BatchMode=yes`;
    const ssh = (remote: string) => ["ssh", ...o.split(" "), "oliver@bulwark-m2", remote];
    const remote = "/tmp/olve-pipelines-v2-helm-apps-beta";
    expect(f.calls).toEqual([
      ["apk", "add", "--no-cache", "openssh-client"],
      `ssh ${o} oliver@bulwark-m2 'sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load' < ${f.tar}`,
      ssh(`rm -rf ${remote}`),
      ["scp", ...o.split(" "), "-r", join(f.tree, "helm"), `oliver@bulwark-m2:${remote}`],
      ssh(
        `cd ${remote} && helm upgrade --install olve-pipelines-v2 . -n apps-beta -f values-beta.yaml -f values-v2-beta.yaml ` +
          `--set slo.enabled=false --set image.repository=docker.io/library/olve-pipelines-v2 --set image.tag=2026.10.21.412 ` +
          `--set image.pullPolicy=Never && cd / && rm -rf ${remote}`,
      ),
      ssh("kubectl -n apps-beta rollout status deploy/olve-pipelines-v2 --timeout=120s"),
      `GET ${def.healthUrl}`,
      `GET ${def.healthUrl}`,
      `GET ${def.healthUrl}`,
    ]);
  });

  test("remote arguments are shell-quoted", async () => {
    const f = fakes();
    const r = await testHandler(steps.helmDeployOverSsh({ ...def, set: { "a.b": "x y", c: "it's" }, healthUrl: undefined }), { source, image }, {
      secrets: { SSH_PRIVATE_KEY: "k\n" },
      ctx: f.ctx,
    });
    expect(r.status).toBe("succeeded");
    const helm = (f.calls[4] as string[]).at(-1)!;
    expect(helm).toContain(`--set 'a.b=x y' --set 'c=it'\\''s' --set image.repository=`);
    expect(f.calls.some((c) => typeof c === "string" && c.startsWith("GET"))).toBe(false);
  });

  test("a missing chart or a failing health check fails the step", async () => {
    const noChart = await testHandler(steps.helmDeployOverSsh(def), { source, image }, { secrets: { SSH_PRIVATE_KEY: "k" }, ctx: fakes({ chart: false }).ctx });
    expect(noChart).toMatchObject({ status: "failed", error: { kind: "handler" } });
    if (noChart.status === "failed") expect(noChart.error.message).toContain("chart directory 'helm' not found");

    const f = fakes({ healthy: 99 });
    const unhealthy = await testHandler(steps.helmDeployOverSsh(def), { source, image }, { secrets: { SSH_PRIVATE_KEY: "k" }, ctx: f.ctx });
    expect(unhealthy).toMatchObject({ status: "failed", error: { kind: "handler" } });
    expect(f.calls.filter((c) => typeof c === "string" && c.startsWith("GET"))).toHaveLength(5);
  });

  test("a failing remote command fails the step and stops the deploy", async () => {
    const f = fakes();
    const exec = f.ctx.exec!;
    f.ctx.exec = async (argv, o) => {
      await exec(argv, o);
      if (argv[0] === "ssh" && argv.at(-1)!.includes("helm upgrade")) throw new Error("`ssh …` failed with exit code 1");
    };
    const r = await testHandler(steps.helmDeployOverSsh(def), { source, image }, { secrets: { SSH_PRIVATE_KEY: "k" }, ctx: f.ctx });
    expect(r).toMatchObject({ status: "failed", error: { kind: "handler" } });
    expect(f.calls.some((c) => Array.isArray(c) && c.at(-1)!.includes("rollout status"))).toBe(false);
  });
});

describe("httpHealthCheck", () => {
  const URL = "http://app.ns.svc.cluster.local/api/health?token=s3cret";
  const fake = (healthyOn: number) => {
    const requests: { url: string; headers?: Record<string, string> }[] = [];
    const retries: { attempts: number; delaySeconds: number }[] = [];
    const logs: string[] = [];
    const ctx = {
      http: {
        expectOk: async (url: string, init?: { headers?: Record<string, string> }) => {
          requests.push({ url, headers: init?.headers });
          if (requests.length < healthyOn) throw new Error(`HTTP 503 from ${url.split("?")[0]}`);
          return new Response("ok");
        },
      } as unknown as Ctx["http"],
      retry: (async (opts, fn) => {
        retries.push({ attempts: opts.attempts, delaySeconds: opts.delaySeconds });
        return fastRetry(opts, fn);
      }) as Ctx["retry"],
      log: (m: string, f?: Record<string, unknown>) => void logs.push(`${m} ${JSON.stringify(f ?? {})}`),
    };
    return { ctx, requests, retries, logs };
  };

  test("retries until 2xx, 5 × 5 s by default (v1 parity), and never logs the query", async () => {
    const f = fake(3);
    await httpHealthCheck(f.ctx, URL);
    expect(f.retries).toEqual([{ attempts: 5, delaySeconds: 5 }]);
    expect(f.requests.map((r) => r.url)).toEqual([URL, URL, URL]);
    expect(f.logs.join("\n")).not.toContain("s3cret");
    expect(f.logs.at(-1)).toContain("health OK");
  });

  test("custom attempts, delay and headers; fails with the last error once attempts run out", async () => {
    const f = fake(99);
    const err = await httpHealthCheck(f.ctx, URL, { attempts: 2, delaySeconds: 1, headers: { authorization: "Bearer t" } }).catch((e: Error) => e);
    expect(err).toBeInstanceOf(Error);
    expect((err as Error).message).toContain("HTTP 503");
    expect(f.retries).toEqual([{ attempts: 2, delaySeconds: 1 }]);
    expect(f.requests).toHaveLength(2);
    expect(f.requests[0]!.headers).toEqual({ authorization: "Bearer t" });
  });
});

describe("library steps in the manifest", () => {
  test("kinds, images, secrets and configs are emitted and schema-valid", () => {
    const Req = model("Req", z.object({ repo: z.string(), sha: z.string() }));
    const p = pipeline({ name: "t", input: Req })
      .stage([steps.gitHubSource({ name: "source", token: TOKEN }), (from) => ({ repo: from.input.repo, sha: from.input.sha })])
      .stage(steps.imageBuild({ name: "build", repository: "app", cacheRepo: "registry.example/app/cache" }))
      .stage(steps.helmDeployOverSsh({ name: "deploy", host: "u@h", sshKey: KEY, release: "app", namespace: "ns", chart: "helm" }));
    const harness = { bun: "1.4.3", sdk: "0.0.0", sdkHash: "sha256:" + "a".repeat(64) };
    const m = describePipeline(p, { handlerNames: [], handlerHashes: {}, harness });
    expect(Manifest.safeParse(m).success).toBe(true);
    const all = m.stages.flatMap((s) => ("steps" in s ? s.steps : []));
    expect(all.map((s) => [s.kind, s.image, s.secrets, s.config, s.input])).toEqual([
      ["gitHubSource", "alpine:3.22", ["GITHUB_TOKEN"], { token: "GITHUB_TOKEN" }, { repo: { ref: "input.repo" }, sha: { ref: "input.sha" } }],
      ["imageBuild", "gcr.io/kaniko-project/executor:debug", [], { repository: "app", cacheRepo: "registry.example/app/cache" }, { source: { ref: "source" } }],
      [
        "helmDeployOverSsh",
        "alpine:3.22",
        ["SSH_PRIVATE_KEY"],
        { host: "u@h", sshKey: "SSH_PRIVATE_KEY", release: "app", namespace: "ns", chart: "helm", values: [], set: {} },
        { source: { ref: "source" }, image: { ref: "build" } },
      ],
    ]);
  });
});
