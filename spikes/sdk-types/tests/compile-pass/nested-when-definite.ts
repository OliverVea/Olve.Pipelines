import { base, checkout, build, test, deployBeta, approveProd, deploy, publishCli, M } from "../fixtures";
import type { InRefs, Ref, Infer } from "@olve/pipelines";
import type { Equal, Expect, AvailOf } from "../type-assert";

// Inside the block, outputs produced earlier in the same block are definite.
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => {
    const inner = p.stage(approveProd).stage(deploy);
    type _ = Expect<Equal<InRefs<AvailOf<typeof inner>>["deploy"], Ref<Infer<typeof M.ProdDeployment>>>>;
    return inner.when({ publishCli: true }, p => p.stage(publishCli));
  });
