// Things that type-check but are WRONG. Each is a reconcile/lint concern (see REPORT.md).
import { base, step, checkout, z } from "../fixtures2";

const s = step({ name: "s", image: "alpine", input: { tag: z.string(), flag: z.boolean() } });
const p = base().stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })]);

// 1. Truthiness on a ref: always truthy at describe time → the proxy silently records "latest".
p.stage([s, i => ({ tag: i.input.publishCli ? "latest" : "beta", flag: true })]);
// 2. `!ref` → recorded as the constant false.
p.stage([s, i => ({ tag: i.input.sha, flag: !i.input.publishCli })]);
// 3. `??` / `||` → always the ref (left side is never nullish/falsy at describe time).
p.stage([s, i => ({ tag: i.input.sha ?? "none", flag: true })]);
// 4. Extra fields in a mapper result are not rejected (contextual return, no freshness check).
p.stage([s, i => ({ tag: i.input.sha, flag: true, extra: 1 })]);
// 5. Side effects / closures in a mapper run once at describe time only.
let n = 0;
p.stage([s, i => (n++, { tag: i.input.sha, flag: n > 0 })]);
// 6. Trigger mappers are real code: comparisons/ternaries type-check (they're on values, not refs).
//    They must run in the map-trigger Job — recording them with proxies would take the wrong branch.
base().onPush(e => ({ sha: e.after, deployTo: e.ref === "refs/heads/main" ? "prod" : "beta" }));
