import { base, checkout, deployBeta } from "../fixtures";

// deploy-beta needs BuildArtifact, but nothing has produced one yet.
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  // @ts-expect-error -- expect: BuildArtifact (input 'artifact' of step 'deploy-beta') is not available at this stage
  .stage(deployBeta);
