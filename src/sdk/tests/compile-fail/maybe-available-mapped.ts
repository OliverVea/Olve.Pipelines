import { base, checkout, build, test, deployBeta, deployProd, publishCli } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(deployProd))
  // @ts-expect-error -- expect: is not assignable to type 'Mappable<
  .stage([publishCli, from => ({ artifact: from.build, prod: from["deploy-prod"] })]);
