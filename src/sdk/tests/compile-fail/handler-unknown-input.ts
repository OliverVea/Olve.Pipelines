import { implement } from "@olivervea/pipelines";
import { deployBeta } from "../fixtures";

// @ts-expect-error -- expect: Property 'source' does not exist
implement(deployBeta.step, async ({ source }, ctx) => ctx.skip("x"));
