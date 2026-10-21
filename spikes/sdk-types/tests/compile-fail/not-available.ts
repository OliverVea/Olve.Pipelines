import { base, checkout, deployBeta } from "../fixtures";

// deploy-beta needs BuildArtifact, but nothing has produced one yet.
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  // @ts-expect-error -- expect: BuildArtifact (input 'artifact' of step 'deploy-beta') is not available at this stage
  .stage(deployBeta);
