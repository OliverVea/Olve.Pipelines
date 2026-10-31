import { implement } from "@olivervea/pipelines";
import { deployBeta } from "../fixtures";

// @ts-expect-error -- expect: Property 'url' is missing
implement(deployBeta.step, async ({ artifact }) => ({ release: "r", version: artifact.version }));
