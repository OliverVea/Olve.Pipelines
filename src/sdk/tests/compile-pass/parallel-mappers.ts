import { base, checkout, checkout2, build, test, lint } from "../fixtures";

const p = base().stage(
  [checkout, from => ({ repo: from.input.repo, sha: from.input.sha })],
  [checkout2, from => ({ repo: "OliverVea/other", sha: from.input.sha })], // literal + ref
);
// Two Source producers: default matching is ambiguous, mappers resolve it.
p.stage([build, from => ({ source: from.checkout })], [test, from => ({ source: from.checkout2 })]);
// Mixed bare + mapped entries, both orders (bare default-matching still needs an unambiguous model:
// lint consumes Source → ambiguous here, so map it too).
p.stage([lint, from => ({ source: from.checkout })], [build, from => ({ source: from.checkout2 })]);

const q = base().stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })]);
q.stage(test, [build, from => ({ source: from.checkout })]);
q.stage([build, from => ({ source: from.checkout })], test);
q.stage(test, lint, [build, from => ({ source: from.checkout })]);
