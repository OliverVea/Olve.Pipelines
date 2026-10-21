import { implement } from "@olve/pipelines";
import { deployBeta } from "../fixtures";

// @ts-expect-error -- expect: Property 'source' does not exist
implement(deployBeta, async ({ source }, ctx) => ctx.skip("x"));
