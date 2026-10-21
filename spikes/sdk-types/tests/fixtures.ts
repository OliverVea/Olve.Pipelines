// Shared models/steps for the compile tests (re-uses the example where possible).
import { pipeline, step, steps, Source, secret } from "@olve/pipelines";
import * as M from "../examples/olve-pipelines/models";
export * from "../examples/olve-pipelines/pipeline";
export { M };

export const base = () => pipeline({ name: "t", input: M.DeployRequest });

/** Second producer of Source, to provoke ambiguity. */
export const checkout2 = steps.gitCheckout({ name: "checkout2" });
/** A step consuming Source. */
export const lint = steps.script({ name: "lint", image: "alpine", script: "lint.sh", input: { source: Source } });
/** A producer of BetaDeployment-shaped data that is actually a ProdDeployment. */
export const fakeProd = step({ name: "fake-prod", image: "alpine", output: M.ProdDeployment });
export const needsBeta = step({ name: "needs-beta", image: "alpine", input: { beta: M.BetaDeployment } });
export const NOPE = secret("NOPE", "not declared anywhere");
