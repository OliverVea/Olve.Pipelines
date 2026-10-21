import { base, checkout, build, deployBeta } from "../fixtures";

// Steps in one stage run in parallel: deploy-beta cannot consume its sibling's output.
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: BuildArtifact (input 'artifact' of step 'deploy-beta') is not available at this stage
  .stage(build, deployBeta);
