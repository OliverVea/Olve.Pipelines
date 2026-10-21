// What the recording proxy and the source lint catch. `bun test tests/`
import { describe as group, expect, test } from "bun:test";
import { MapperError, recordMapper, refPath, rootProxy } from "../sdk/record";

const roots = new Set(["input", "checkout", "build", "test"]);
// Proxy only (no source lint) — shows the raw limits of runtime recording.
const proxyOnly = (fn: (r: any) => unknown) => fn(rootProxy(roots));

group("recorded as paths", () => {
  test("property access + literals + nesting", () => {
    expect(recordMapper((r) => ({ source: r.checkout, sha: r.input.sha, n: 3, meta: { repo: r.input.repo, tag: "x" } }), roots)).toEqual({
      source: { path: "checkout" }, sha: { path: "input.sha" }, n: { const: 3 },
      meta: { object: { repo: { path: "input.repo" }, tag: { const: "x" } } },
    });
  });
});

group("proxy throws (caught at runtime)", () => {
  const cases: [string, (r: any) => unknown][] = [
    ["template string", (r) => ({ tag: `v${r.input.sha}` })],
    ["concatenation", (r) => ({ tag: "v" + r.input.sha })],
    ["String()", (r) => ({ tag: String(r.input.sha) })],
    ["method call", (r) => ({ tag: r.input.sha.slice(0, 7) })],
    ["spread", (r) => ({ ...r.checkout })],
    ["JSON.stringify", (r) => ({ s: JSON.stringify(r.input) })],
    ["arithmetic", (r) => ({ n: r.input.count * 2 })],
    ["relational", (r) => ({ b: r.input.count > 2 })],
    ["unavailable root", (r) => ({ x: r.deployProd })],
  ];
  for (const [name, fn] of cases) test(name, () => expect(() => proxyOnly(fn)).toThrow(MapperError));
});

group("proxy CANNOT see these (silent wrong mapping) — source lint catches them", () => {
  test("=== ternary silently picks the false branch", () => {
    const fn = (r: any) => ({ src: r.input.deployTo === "prod" ? r.build : r.test });
    expect(refPath((proxyOnly(fn) as any).src)).toBe("test"); // always `test`, whatever deployTo is
    expect(() => recordMapper(fn, roots)).toThrow(/equality|ternary/);
  });
  test("truthiness silently picks the true branch", () => {
    const fn = (r: any) => ({ src: r.input.publishCli ? r.build : r.test });
    const rec = proxyOnly(fn) as any;
    expect(refPath(rec.src)).toBe("build"); // always `build`, whatever publishCli is
    expect(() => recordMapper(fn, roots)).toThrow(/ternary/);
  });
  test("&& / || / !", () => {
    expect(() => recordMapper((r) => ({ x: r.input.a && r.input.b }), roots)).toThrow(/logical/);
    expect(() => recordMapper((r) => ({ x: r.input.a || "d" }), roots)).toThrow(/logical/);
    expect(() => recordMapper((r) => ({ x: !r.input.a }), roots)).toThrow(/negation/);
  });
  test("the spec's onPush example cannot be recorded", () => {
    const onPush = (e: any) => ({ repo: e.repository, sha: e.after, deployTo: e.ref === "refs/heads/main" ? "prod" : "beta" });
    const silently = onPush(rootProxy()) as any;
    expect(silently.deployTo).toBe("beta"); // proxy === string is false → always "beta"
    expect(() => recordMapper(onPush)).toThrow(/equality|ternary/);
  });
  test("lint false-positive check: strings containing operators are fine", () => {
    expect(recordMapper((r) => ({ a: "x ? y : z && !w", b: r.input.sha }), roots)).toEqual({ a: { const: "x ? y : z && !w" }, b: { path: "input.sha" } });
  });
});
