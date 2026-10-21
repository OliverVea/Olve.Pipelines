import { base, checkout } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .when({ deployTo: "prod" }, p => p
    // @ts-expect-error -- expect: duplicate step name 'checkout'
    .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })]));
