import { base, checkout, checkout2, build, test, lint } from "../fixtures";

const p = base().stage(
  [checkout, i => ({ repo: i.input.repo, sha: i.input.sha })],
  [checkout2, i => ({ repo: "OliverVea/other", sha: i.input.sha })], // literal + ref
);
// Two Source producers: default matching is ambiguous, mappers resolve it.
p.stage([build, i => ({ source: i.checkout })], [test, i => ({ source: i.checkout2 })]);
// Mixed bare + mapped entries, both orders (bare default-matching still needs an unambiguous model:
// lint consumes Source → ambiguous here, so map it too).
p.stage([lint, i => ({ source: i.checkout })], [build, i => ({ source: i.checkout2 })]);

const q = base().stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })]);
q.stage(test, [build, i => ({ source: i.checkout })]);
q.stage([build, i => ({ source: i.checkout })], test);
q.stage(test, lint, [build, i => ({ source: i.checkout })]);
