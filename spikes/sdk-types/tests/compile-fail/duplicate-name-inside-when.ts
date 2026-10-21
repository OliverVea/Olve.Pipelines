import { base, checkout } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .when({ deployTo: "prod" }, p => p
    // @ts-expect-error -- expect: duplicate step name 'checkout'
    .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })]));
