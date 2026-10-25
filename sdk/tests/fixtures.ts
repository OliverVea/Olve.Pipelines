// Shared models/steps for the compile tests (re-uses the example pipeline).
import { implement, pipeline, step, Source, secret, z } from "@olivervea/pipelines";
import * as M from "../examples/self-deploy/models";
export * from "../examples/self-deploy/pipeline";
export { M };

export const base = () => pipeline({ name: "t", input: M.DeployRequest });

/** Second producer of Source, to provoke ambiguity. */
export const checkout2 = step({ name: "checkout2", description: "", image: "alpine/git", input: { repo: z.string(), sha: z.string() }, output: Source });
/** A step consuming Source that carries its implementation. */
export const lint = implement(step({ name: "lint", description: "", kind: "lint", image: "alpine", input: { source: Source } }), () => {});
/** A producer of BetaDeployment-shaped data that is actually a ProdDeployment. */
export const fakeProd = step({ name: "fake-prod", description: "", image: "alpine", output: M.ProdDeployment });
export const needsBeta = step({ name: "needs-beta", description: "", image: "alpine", input: { beta: M.BetaDeployment } });
export const NOPE = secret("NOPE", "not declared anywhere");

