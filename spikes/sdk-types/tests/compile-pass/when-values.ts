import { base, test, checkout } from "../fixtures";

base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .when({ deployTo: ["beta", "prod"] }, p => p.stage(test))
  .when({ deployTo: "beta", publishCli: false }, p => p)
  .when({}, p => p); // empty condition = always (reconcile could warn)
