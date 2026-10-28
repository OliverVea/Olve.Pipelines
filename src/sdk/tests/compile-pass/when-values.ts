import { base, test, checkout } from "../fixtures";

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .when({ deployTo: ["beta", "prod"] }, p => p.stage(test))
  .when({ deployTo: "beta", publishCli: false }, p => p)
  .when({}, p => p); // empty condition = always (reconcile could warn)
