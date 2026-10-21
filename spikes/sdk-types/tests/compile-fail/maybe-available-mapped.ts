import { base, checkout, build, test, deployBeta, approveProd, deploy, publishCli } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(approveProd).stage(deploy))
  // @ts-expect-error -- expect: is not assignable to type 'Mappable<
  .stage([publishCli, i => ({ artifact: i.build, prod: i.deploy })]);
