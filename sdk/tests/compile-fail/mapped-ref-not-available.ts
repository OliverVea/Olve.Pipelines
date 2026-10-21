import { base, checkout, build, deployBeta } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  // @ts-expect-error -- expect: Property 'build' does not exist
  .stage([deployBeta, from => ({ artifact: from.build })]);
