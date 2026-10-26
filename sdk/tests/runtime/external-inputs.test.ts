import { describe, expect, test } from "bun:test";
import { callback, external, implement, model, pipeline, poll, step, z } from "../../src/index.ts";
import { describePipeline, DescribeError } from "../../src/describe/describe.ts";
import { Manifest } from "../../src/describe/manifest-schema.ts";
import { validateInput } from "../../src/harness/validate.ts";
import { testHandler } from "../../src/testing/index.ts";

const harness = { bun: "1.4.3", sdk: "0.0.0", sdkHash: "sha256:" + "a".repeat(64) };
const H = "sha256:" + "b".repeat(64);
const Req = model("Req", z.object({ sha: z.string() }));
const Art = model("Art", z.object({ version: z.string() }));
const Verdict = model("Verdict", z.object({ accepted: z.boolean(), comment: z.string().optional() }));
const Choice = model("Choice", z.object({ target: z.enum(["beta", "prod"]), notes: z.string().default("") }));

const build = step({ name: "build", description: "", image: "alpine", input: { req: Req }, output: Art });
const pick = step({
  name: "pick",
  description: "",
  image: "alpine",
  input: { art: Art, choice: external(Choice, { title: "Where to?", description: "Pick a target." }) },
});
const reviewStep = step({ name: "review", description: "", image: "alpine", input: { verdict: external(Verdict, { title: "Screenshots accepted?" }) } });
const review = implement(reviewStep, ({ verdict }) => {
  if (!verdict.accepted) throw new Error(verdict.comment ?? "rejected");
});

const describeIt = (p: unknown, handlers = ["build", "pick", "review"]) =>
  describePipeline(p, { handlerNames: handlers, handlerHashes: Object.fromEntries(handlers.map((n) => [n, H])), harness });

const stepsOf = (m: ReturnType<typeof describeIt>) => m.stages.flatMap((s) => ("steps" in s ? s.steps : []));

describe("external inputs in the manifest", () => {
  test("are recorded apart from the mapped inputs, by model, and the manifest is schema-valid", () => {
    const m = describeIt(pipeline({ name: "t", input: Req }).stage(build).stage(pick).stage(reviewStep));
    expect(Manifest.safeParse(m).success).toBe(true);
    const [, picked, reviewed] = stepsOf(m);
    expect(picked!.input).toEqual({ art: { ref: "build" } });
    expect(Object.keys(picked!.inputTypes)).toEqual(["art"]);
    expect(picked!.externalInputs).toEqual({ choice: { model: "Choice", title: "Where to?", description: "Pick a target.", from: [{ kind: "person" }] } });
    expect(reviewed!.externalInputs).toEqual({ verdict: { model: "Verdict", title: "Screenshots accepted?", description: "", from: [{ kind: "person" }] } });
    expect(Object.keys(m.models)).toContain("Choice");
  });

  test("a mapper doesn't provide them", () => {
    const m = describeIt(pipeline({ name: "t", input: Req }).stage(build).stage([pick, (from) => ({ art: from.build })]), ["build", "pick"]);
    expect(stepsOf(m)[1]!.externalInputs?.choice?.model).toBe("Choice");
  });

  test("a failure handler can't wait for input", () => {
    const Ctx = model("FailureContext", z.object({ reason: z.string() }));
    const handler = step({ name: "on-fail", description: "", image: "alpine", input: { ctx: Ctx, ok: external(Verdict, { title: "?" }) } });
    let problems: string[] = [];
    try {
      describeIt(pipeline({ name: "t", input: Req }).stage(build).onFailure(handler as never), ["build", "on-fail"]);
    } catch (e) {
      if (e instanceof DescribeError) problems = [...e.problems];
      else throw e;
    }
    expect(problems.some((p) => /cannot wait for input/.test(p))).toBe(true);
  });
});

describe("the harness", () => {
  test("validates an answered external input like any input, with defaults", () => {
    const r = validateInput(pick, { art: { version: "1" }, choice: { target: "prod" } });
    expect(r.ok).toBe(true);
    if (r.ok) expect((r.value as { choice: unknown }).choice).toEqual({ target: "prod", notes: "" });
    expect(validateInput(pick, { art: { version: "1" } }).ok).toBe(false);
  });
});

describe("a handler", () => {
  test("gets the answered value", async () => {
    expect((await testHandler(review, { verdict: { accepted: true } })).status).toBe("succeeded");
    const no = await testHandler(review, { verdict: { accepted: false, comment: "button moved" } });
    expect(no.status).toBe("failed");
    expect(JSON.stringify(no)).toContain("button moved");
  });
});

describe("callback and poll sources", () => {
  const signalled = step({
    name: "signalled",
    description: "",
    image: "alpine",
    input: {
      art: Art,
      verdict: external(Verdict, { title: "Called back?", from: [callback()] }),
      status: external(Verdict, { title: "Polled?", from: [poll({ url: "https://example.com/s/{run.id}", valuePath: "result" })] }),
    },
  });

  test("are recorded with their settings", () => {
    const m = describeIt(pipeline({ name: "t", input: Req }).stage(build).stage(signalled), ["build", "signalled"]);
    expect(Manifest.safeParse(m).success).toBe(true);
    expect(stepsOf(m)[1]!.externalInputs).toEqual({
      verdict: { model: "Verdict", title: "Called back?", description: "", from: [{ kind: "callback" }] },
      status: { model: "Verdict", title: "Polled?", description: "", from: [{ kind: "poll", url: "https://example.com/s/{run.id}", valuePath: "result", intervalSeconds: 30 }] },
    });
  });

  test("bad sources are describe problems", () => {
    const bad = step({
      name: "bad",
      description: "",
      image: "alpine",
      input: { "a.b": external(Verdict, { title: "?", from: [callback(), callback()] }), p: external(Verdict, { title: "?", from: [poll({ url: "ftp://x", valuePath: "v", intervalSeconds: 1 })] }) },
    });
    let problems: string[] = [];
    try {
      describeIt(pipeline({ name: "t", input: Req }).stage(bad), ["bad"]);
    } catch (e) {
      if (e instanceof DescribeError) problems = [...e.problems];
      else throw e;
    }
    expect(problems.some((p) => /listed once/.test(p))).toBe(true);
    expect(problems.some((p) => /part of the URL/.test(p))).toBe(true);
    expect(problems.some((p) => /http\(s\)/.test(p))).toBe(true);
    expect(problems.some((p) => /at least 10/.test(p))).toBe(true);
  });

  test("ctx.inputUrl gives the run's callback URL, and says why when there is none", async () => {
    const send = implement(step({ name: "send", description: "", image: "alpine", input: { art: Art } }), async (_, ctx) => {
      ctx.log("url", { url: ctx.inputUrl(signalled, "verdict") });
    });
    const ok = await testHandler(send, { art: { version: "1" } }, { callbackUrls: { "signalled/verdict": "https://hooks/x" } });
    expect(ok.status).toBe("succeeded");
    expect(ok.logs.join("\n")).toContain("https://hooks/x");
    const missing = await testHandler(send, { art: { version: "1" } });
    expect(missing.status).toBe("failed");
    expect(JSON.stringify(missing)).toContain("no callback URL");
  });
});
