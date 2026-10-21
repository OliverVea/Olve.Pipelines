import { base, checkout, test, build } from "../fixtures";

// A bare step followed by a mapped entry (the order that originally broke inference).
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: Property 'checkoutx' does not exist
  .stage(test, [build, i => ({ source: i.checkoutx })]);
