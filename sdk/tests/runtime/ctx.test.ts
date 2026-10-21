// ctx pieces, exercised through testHandler (the same validation path as a real run).
/* eslint-disable @typescript-eslint/no-explicit-any */
import { afterAll, describe, expect, test } from "bun:test";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { implement, model, secret, step, steps, z, fileRef, Source } from "../../src/index.ts";
import { testFileRef, testHandler } from "../../src/testing/index.ts";
import { createSh, detectShell, ShellNotFoundError, isEmbeddedScript, CommandError } from "../../src/ctx/sh.ts";
import { sha256File } from "../../src/util/hash.ts";
import checkoutImpl from "../../examples/self-deploy/handlers/checkout.ts";
import buildImpl from "../../examples/self-deploy/handlers/build.ts";
import deployBetaImpl from "../../examples/self-deploy/handlers/deploy-beta.ts";
import { test as testStep } from "../../examples/self-deploy/pipeline.ts";
import { EXAMPLE, SHA, tmp, withEnv } from "./helpers.ts";

const Out = model("Out", z.object({ text: z.string() }));
const KEY = secret("API_KEY", "an api key");
const plain = step({ name: "plain", image: "alpine", input: { cmd: z.string() }, output: Out, secrets: [KEY] });

const servers: { stop(force?: boolean): void }[] = [];
afterAll(() => servers.forEach((s) => s.stop(true)));

describe("ctx.sh / ctx.exec", () => {
  test("command string, captured text, env, failures carry the exit code", async () => {
    const impl = implement(plain, async ({ cmd }, ctx) => {
      await ctx.sh("true");
      const text = await ctx.sh.text(cmd, { env: { GREETING: "hello" } });
      await ctx.sh("exit 3").catch((e: CommandError) => ctx.log("failed", { code: e.exitCode }));
      return { text };
    });
    const r = await testHandler(impl, { cmd: 'echo "$GREETING world"' });
    expect(r).toMatchObject({ status: "succeeded", output: { text: "hello world" } });
    expect(r.logs).toContain("failed code=3");
  });
  test("embedded scripts are copied out and run with args; harness credentials are not inherited", async () => {
    const dir = tmp("script");
    const script = join(dir, "s.sh");
    writeFileSync(script, '#!/bin/sh\necho "args:$1,$2 url:${OLVE_RESULT_URL:-unset}"\n');
    expect(isEmbeddedScript(script)).toBe(true);
    expect(isEmbeddedScript("echo hi")).toBe(false);
    expect(isEmbeddedScript("/$bunfs/root/x.sh")).toBe(true);
    const impl = implement(plain, async (_i, ctx) => ({ text: await ctx.sh.text(script, { args: ["a", "b"] }) }));
    const r = await withEnv({ OLVE_RESULT_URL: "http://secret/?X-Amz-Credential=1" }, () => testHandler(impl, { cmd: "" }));
    expect(r).toMatchObject({ status: "succeeded", output: { text: "args:a,b url:unset" } });
    expect(existsSync(join(r.dir, "tmp", "scripts", "1-s.sh"))).toBe(true);
  });
  test("shell detection: first existing candidate, clear error when none", async () => {
    expect(detectShell()).toBe("/bin/sh");
    expect(detectShell(["/nope/sh", "/bin/sh"])).toBe("/bin/sh");
    expect(() => detectShell(["/nope/sh"])).toThrow(ShellNotFoundError);
    const { sh } = createSh({ tmpDir: tmp("noshell"), signal: new AbortController().signal, shellCandidates: ["/nope/sh", "/busybox/nope"] });
    await expect(sh("echo hi")).rejects.toThrow(/no shell found in this image/);
  });
  test("exec runs binaries without a shell", async () => {
    const impl = implement(plain, async (_i, ctx) => {
      await ctx.exec(["/bin/true"]);
      await expect(ctx.exec(["/bin/false"])).rejects.toThrow(/exit code 1/);
      await expect(ctx.exec(["/does/not/exist"])).rejects.toThrow(/cannot start/);
      return { text: "ok" };
    });
    expect((await testHandler(impl, { cmd: "" })).status).toBe("succeeded");
  });
  test("signal: cancellation kills the running command", async () => {
    const ac = new AbortController();
    const impl = implement(plain, async (_i, ctx) => {
      setTimeout(() => ac.abort("SIGTERM"), 100);
      await ctx.sh("sleep 5");
      return { text: "not reached" };
    });
    const t0 = Date.now();
    const r = await testHandler(impl, { cmd: "" }, { signal: ac.signal });
    expect(Date.now() - t0).toBeLessThan(3000);
    expect(r).toMatchObject({ status: "failed", error: { kind: "handler" }, exitCode: 143 });
  });
});

describe("ctx.secrets", () => {
  test("typed to the declared set; read from options or env; clear error when missing", async () => {
    const impl = implement(plain, async (_i, ctx) => ({ text: ctx.secrets.API_KEY }));
    expect(await testHandler(impl, { cmd: "" }, { secrets: { API_KEY: "k1" } })).toMatchObject({ output: { text: "k1" } });
    expect(await withEnv({ API_KEY: "k2" }, () => testHandler(impl, { cmd: "" }))).toMatchObject({ output: { text: "k2" } });
    const r = await withEnv({ API_KEY: undefined }, () => testHandler(impl, { cmd: "" }));
    expect(r).toMatchObject({ status: "failed", error: { kind: "handler" } });
    expect((r as any).error.message).toMatch(/secret API_KEY is not set/);
  });
});

describe("ctx.output and ctx.files.fetch", () => {
  const Files = model("Files", z.object({ a: fileRef(), b: fileRef(), d: fileRef() }));
  const producer = step({ name: "producer", image: "alpine", output: Files });
  const consumer = step({ name: "consumer", image: "alpine", input: { files: Files }, output: Out });

  test("write / file / dir produce refs with run, step, attempt, path, sha256 (dirs as deterministic tars)", async () => {
    const src = tmp("out");
    writeFileSync(join(src, "b.bin"), "bee");
    mkdirSync(join(src, "tree/sub"), { recursive: true });
    writeFileSync(join(src, "tree/sub/x.txt"), "x");
    const impl = implement(producer, async (_i, ctx) => ({
      a: await ctx.output.write("nested/a.txt", "aaa"),
      b: await ctx.output.file(join(src, "b.bin")),
      d: await ctx.output.dir(join(src, "tree"), { name: "tree" }),
    }));
    const r = await testHandler(impl, {}, { run: { number: 7, attempt: 2 } });
    expect(r.status).toBe("succeeded");
    const out = (r as any).output;
    expect(out.a).toEqual({ run: 7, step: "producer", attempt: 2, path: "nested/a.txt", sha256: sha256File(join(r.filesDir, "nested/a.txt")) });
    expect(out.b.path).toBe("b.bin");
    expect(out.d.path).toBe("tree/");
    expect(existsSync(join(r.filesDir, "tree.tar"))).toBe(true);
    // determinism: same tree → same tar hash
    const again = await testHandler(impl, {}, { run: { number: 7, attempt: 2 } });
    expect((again as any).output.d.sha256).toBe(out.d.sha256);
  });
  test("output names are validated and unique", async () => {
    const bad = implement(producer, async (_i, ctx) => ({ a: await ctx.output.write("../x", "1"), b: null as any, d: null as any }));
    expect((await testHandler(bad, {})) as any).toMatchObject({ status: "failed", error: { kind: "handler", message: expect.stringMatching(/not a valid relative file name/) } });
    const dup = implement(producer, async (_i, ctx) => {
      await ctx.output.write("x", "1");
      await ctx.output.write("x", "2");
      return null as any;
    });
    expect(((await testHandler(dup, {})) as any).error.message).toMatch(/already exists/);
  });

  test("fetch from a local pool dir (OLVE_FILES_DIR), from pre-signed URLs, and sha verification", async () => {
    const prod = await testHandler(
      implement(producer, async (_i, ctx) => ({
        a: await ctx.output.write("a.txt", "aaa"),
        b: await ctx.output.write("b.txt", "bbb"),
        d: await ctx.output.dir(EXAMPLE + "/scripts", { name: "scripts" }),
      })),
      {},
      { run: { number: 3 } },
    );
    const files = (prod as any).output;
    // pool layout: <dir>/<run>/<step>/<attempt>/files/<object path>
    const pool = tmp("pool");
    mkdirSync(join(pool, "3/producer/1"), { recursive: true });
    await Bun.$`cp -r ${prod.filesDir} ${join(pool, "3/producer/1/files")}`;
    const reader = implement(consumer, async ({ files }, ctx) => {
      const a = await ctx.files.fetch(files.a);
      const d = await ctx.files.fetch(files.d);
      return { text: readFileSync(a, "utf8") + "|" + readFileSync(join(d, "deploy.sh"), "utf8").split("\n")[0] };
    });
    const r1 = await withEnv({ OLVE_FILES_DIR: pool, OLVE_FILE_URLS: undefined }, () => testHandler(reader, { files }, { run: { number: 4 } }));
    expect(r1).toMatchObject({ status: "succeeded", output: { text: "aaa|#!/bin/sh" } });

    // pre-signed URL map (what the service provides): key = <run>/<step>/<attempt>/<path>
    const seen: string[] = [];
    const server = Bun.serve({
      port: 0,
      fetch(req) {
        const u = new URL(req.url);
        seen.push(u.pathname + u.search);
        const obj = u.pathname.slice(1);
        return new Response(Bun.file(join(prod.filesDir, obj)));
      },
    });
    servers.push(server);
    const base = `http://localhost:${server.port}`;
    const urls = { "3/producer/1/a.txt": `${base}/a.txt?X-Amz-Signature=s`, "3/producer/1/scripts/": `${base}/scripts.tar?X-Amz-Signature=s` };
    const r2 = await withEnv({ OLVE_FILES_DIR: undefined, OLVE_FILE_URLS: JSON.stringify(urls) }, () => testHandler(reader, { files }));
    expect(r2).toMatchObject({ status: "succeeded", output: { text: "aaa|#!/bin/sh" } });
    expect(seen).toEqual(["/a.txt?X-Amz-Signature=s", "/scripts.tar?X-Amz-Signature=s"]);

    // base URL + token
    const auth: (string | null)[] = [];
    const s2 = Bun.serve({
      port: 0,
      fetch(req) {
        auth.push(req.headers.get("authorization"));
        const obj = new URL(req.url).pathname.split("/").slice(4).join("/");
        return new Response(Bun.file(join(prod.filesDir, obj)));
      },
    });
    servers.push(s2);
    const r3 = await withEnv({ OLVE_FILE_URLS: undefined, OLVE_FILES_BASE_URL: `http://localhost:${s2.port}/`, OLVE_FILES_TOKEN: "tok" }, () => testHandler(reader, { files }));
    expect(r3.status).toBe("succeeded");
    expect(auth).toEqual(["Bearer tok", "Bearer tok"]);

    // tampered content → sha mismatch
    const wrong = { ...files, a: { ...files.a, sha256: "0".repeat(64) } };
    const r4 = await withEnv({ OLVE_FILES_DIR: pool }, () => testHandler(reader, { files: wrong }));
    expect((r4 as any).error.message).toMatch(/has sha256 .* expected 0000/);
    // no source at all
    const r5 = await withEnv({ OLVE_FILES_DIR: undefined, OLVE_FILE_URLS: undefined, OLVE_FILES_BASE_URL: undefined }, () => testHandler(reader, { files }));
    expect((r5 as any).error.message).toMatch(/no source for 3\/producer\/1\/a.txt/);
  });
  test("refs are validated before use (no traversal)", async () => {
    const reader = implement(consumer, async ({ files }, ctx) => ({ text: await ctx.files.fetch(files.a) }));
    const evil = testFileRef({ step: "producer", path: "../../etc/passwd" });
    const r = await testHandler(reader, { files: { a: evil, b: evil, d: evil } });
    expect(r).toMatchObject({ status: "failed", error: { kind: "input" }, exitCode: 65 });
  });
  test("an output ref claiming this attempt must point to a produced file", async () => {
    const liar = implement(producer, async () => ({
      a: testFileRef({ step: "producer", path: "missing.txt" }),
      b: testFileRef({ step: "other", path: "ok.txt" }),
      d: testFileRef({ step: "other", path: "d/" }),
    }));
    const r = await testHandler(liar, {});
    expect(r).toMatchObject({ status: "failed", error: { kind: "output" }, exitCode: 66 });
    expect((r as any).error.message).toMatch(/no such output file/);
  });
});

describe("ctx.http and ctx.retry", () => {
  test("getJson, expectOk with a readable error, retry with backoff", async () => {
    let hits = 0;
    const server = Bun.serve({
      port: 0,
      fetch(req) {
        const p = new URL(req.url).pathname;
        if (p === "/json") return Response.json({ ok: true });
        if (p === "/flaky") return ++hits < 3 ? new Response("busy", { status: 503 }) : new Response("ok");
        return new Response("nope", { status: 404 });
      },
    });
    servers.push(server);
    const base = `http://localhost:${server.port}`;
    const impl = implement(plain, async (_i, ctx) => {
      const j = await ctx.http.getJson<{ ok: boolean }>(`${base}/json`);
      const err = await ctx.http.expectOk(`${base}/missing?token=secret`).then(() => "", (e: Error) => e.message);
      await ctx.retry({ attempts: 5, delaySeconds: 0.01, backoff: 2 }, () => ctx.http.expectOk(`${base}/flaky`));
      await expect(ctx.retry({ attempts: 2, delaySeconds: 0 }, async () => { throw new Error("always"); })).rejects.toThrow("always");
      return { text: `${j.ok}|${err}` };
    });
    const r = await testHandler(impl, { cmd: "" });
    expect(r).toMatchObject({ status: "succeeded" });
    expect((r as any).output.text).toBe(`true|HTTP 404 Not Found from ${base}/missing?…: nope`);
    expect(hits).toBe(3);
    expect(r.logs.filter((l) => l.startsWith("retrying"))).toHaveLength(3);
  });
});

describe("testHandler on the example handlers", () => {
  test("checkout → build → deploy-beta chain through file refs, run info, skip", async () => {
    const co = await testHandler(checkoutImpl, { repo: "OliverVea/Olve.Pipelines", sha: SHA }, { run: { number: 412, version: "2026.10.21.412" } });
    expect(co).toMatchObject({ status: "succeeded", output: { repo: "OliverVea/Olve.Pipelines", sha: SHA, tree: { run: 412, step: "checkout", attempt: 1, path: "tree/" } } });
    expect(co.logs[0]).toMatch(/^checked out repo=OliverVea\/Olve.Pipelines/);
    const source = (co as any).output;
    const b = await testHandler(buildImpl, { source }, {
      run: { number: 412, version: "2026.10.21.412" },
      ctx: { files: { fetch: async () => join(EXAMPLE, "handlers") } },
    });
    expect(b).toMatchObject({ status: "succeeded", output: { version: "2026.10.21.412+0123456", image: { path: "image.tar" }, chart: { path: "chart/" } } });
    const pool = tmp("chain");
    mkdirSync(join(pool, "412/build/1"), { recursive: true });
    await Bun.$`cp -r ${b.filesDir} ${join(pool, "412/build/1/files")}`;
    const artifact = (b as any).output;
    const beta = await withEnv({ OLVE_FILES_DIR: pool }, () => testHandler(deployBetaImpl, { artifact }, { secrets: { SSH_PRIVATE_KEY: "key" } }));
    expect(beta).toMatchObject({ status: "succeeded", output: { release: "olve-pipelines-beta", version: artifact.version } });
    const skipped = await withEnv({ EXAMPLE_SKIP_BETA: "1" }, () => testHandler(deployBetaImpl, { artifact }));
    expect(skipped).toMatchObject({ status: "skipped", reason: "EXAMPLE_SKIP_BETA is set", exitCode: 0 });
    const badUrl = await withEnv({ EXAMPLE_BETA_URL: "not a url", OLVE_FILES_DIR: pool }, () => testHandler(deployBetaImpl, { artifact }, { secrets: { SSH_PRIVATE_KEY: "k" } }));
    expect(badUrl).toMatchObject({ status: "failed", error: { kind: "output" }, exitCode: 66 });
  });
  test("library script step: env, input.json, output.json, file placeholders", async () => {
    const r = await testHandler(testStep, { source: testFileRef as any } as any);
    expect(r).toMatchObject({ status: "failed", error: { kind: "input" } });
    const source = { repo: "r", sha: SHA, tree: testFileRef({ step: "checkout", path: "tree/" }) };
    expect(await testHandler(testStep, { source })).toMatchObject({ status: "succeeded", output: { passed: 3, failed: 0 } });
    const dir = tmp("lib");
    const sh = join(dir, "files.sh");
    writeFileSync(sh, '#!/bin/sh\nset -eu\necho "$INPUT_DEPLOY_TO" > "$TMPDIR_OUT/v.txt"\nprintf \'{"tag":"%s-%s","img":{"$file":"%s"}}\' "$INPUT_DEPLOY_TO" "$TAG" "$TMPDIR_OUT/v.txt" > "$OLVE_OUTPUT"\n');
    const Img = model("Img", z.object({ tag: z.string(), img: fileRef() }));
    const s = steps.script({ name: "lib", image: "alpine", script: sh, input: { deployTo: z.enum(["beta", "prod"]) }, output: Img, env: { TMPDIR_OUT: dir, TAG: ({ deployTo }, run) => `${deployTo}.${run.number}` } });
    const r2 = await testHandler(s, { deployTo: "prod" }, { run: { number: 9 } });
    expect(r2).toMatchObject({ status: "succeeded", output: { tag: "prod-prod.9", img: { path: "v.txt", run: 9, step: "lib" } } });
  });
  test("input is validated like input.json (unknown keys, wrong types)", async () => {
    const r = await testHandler(checkoutImpl, { repo: "r", sha: SHA, extra: 1 } as any);
    expect(r).toMatchObject({ status: "failed", error: { kind: "input", message: expect.stringMatching(/input.extra: not an input/) }, exitCode: 65 });
  });
  test("Source is the library source model", () => expect(Source.name).toBe("Source"));
});
