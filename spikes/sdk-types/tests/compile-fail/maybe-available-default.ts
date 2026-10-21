import { base, checkout, build, test, deployBeta, approveProd, deploy, publishCli } from "../fixtures";

// deploy runs only when deployTo == prod, so after the block its ProdDeployment is maybe-available.
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(approveProd).stage(deploy))
  // @ts-expect-error -- expect: ProdDeployment (input 'prod' of step 'publish-cli') is only maybe-available (produced inside a when block); declare it optional(ProdDeployment)
  .stage(publishCli);
