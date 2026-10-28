import { base, checkout, build, test, deployBeta, deployProd, summary, M } from "../fixtures";
import { step, optional, type InRefs, type Ref, type Infer, type StepInput } from "@olivervea/pipelines";
import type { Equal, Expect, AvailOf } from "../type-assert";

// Outputs produced inside a when are maybe-available afterwards: optional() inputs accept them.
const p = base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(deployProd));

const report = step({ name: "report", description: "", image: "alpine", input: { beta: M.BetaDeployment, prod: optional(M.ProdDeployment) } });
p.stage(report); // beta definite, prod maybe → fine because prod is optional()
// (summary also wants optional(CliRelease), which nothing produces here → error; see compile-fail/optional-not-available.ts)

type R = InRefs<AvailOf<typeof p>>;
type _deploy = Expect<Equal<R["deploy-prod"], Ref<Infer<typeof M.ProdDeployment> | undefined>>>;
type _beta = Expect<Equal<R["deploy-beta"], Ref<Infer<typeof M.BetaDeployment>>>>;
type _test = Expect<Equal<R["test"], Ref<Infer<typeof M.TestReport>>>>;

// Handler view of optional inputs: T | undefined.
type SI = StepInput<(typeof summary)["input"]>;
type _prod = Expect<Equal<SI["prod"], Infer<typeof M.ProdDeployment> | undefined>>;
