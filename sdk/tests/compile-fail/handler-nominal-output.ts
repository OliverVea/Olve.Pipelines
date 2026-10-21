import { implement, type Infer } from "@olve/pipelines";
import { deployBeta, M } from "../fixtures";

declare const prod: Infer<typeof M.ProdDeployment>;
// A ProdDeployment *value* cannot be returned as a BetaDeployment.
// @ts-expect-error -- expect: is not assignable to type 'HandlerResult<
implement(deployBeta, async () => prod);
