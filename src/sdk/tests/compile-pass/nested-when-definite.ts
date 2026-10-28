import { base, checkout, build, test, deployBeta, deployProd, publishCli, M } from "../fixtures";
import type { InRefs, Ref, Infer } from "@olivervea/pipelines";
import type { Equal, Expect, AvailOf } from "../type-assert";

// Inside the block, outputs produced earlier in the same block are definite.
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => {
    const inner = p.stage(deployProd);
    type _ = Expect<Equal<InRefs<AvailOf<typeof inner>>["deploy-prod"], Ref<Infer<typeof M.ProdDeployment>>>>;
    return inner.when({ publishCli: true }, p => p.stage(publishCli));
  });
