import { base, checkout, test, build } from "../fixtures";

// A bare step followed by a mapped entry (the order that originally broke inference).
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  // @ts-expect-error -- expect: Property 'checkoutx' does not exist
  .stage(test, [build, from => ({ source: from.checkoutx })]);
