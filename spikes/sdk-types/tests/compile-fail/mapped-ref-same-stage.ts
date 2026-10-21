import { base, checkout, build, deployBeta } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: Property 'build' does not exist
  .stage(build, [deployBeta, i => ({ artifact: i.build })]);
