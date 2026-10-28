import { base, checkout, build, test, deployBeta, deployProd, publishCli } from "../fixtures";

// deploy-prod runs only when deployTo == prod, so after the block its ProdDeployment is maybe-available.
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(deployProd))
  // @ts-expect-error -- expect: ProdDeployment (input 'prod' of step 'publish-cli') is only maybe-available (produced inside a when block); declare it optional(ProdDeployment)
  .stage(publishCli);
