import { implement } from "@olivervea/pipelines";
import { deployBeta } from "../fixtures";

// @ts-expect-error -- expect: Property 'url' is missing
implement(deployBeta, async ({ artifact }) => ({ release: "r", version: artifact.version }));
