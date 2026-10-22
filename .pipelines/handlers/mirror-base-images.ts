import { implement } from "@olve/pipelines";
import { MIRROR, mirrorBaseImages } from "../pipeline";

// The Dockerfile's FROMs. One missing here still builds: kaniko falls back to the upstream registry.
const IMAGES = [
  "mcr.microsoft.com/dotnet/sdk:10.0-noble",
  "mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled",
  "docker.io/library/node:22-slim",
];
const TRIES = 2;

export default implement(mirrorBaseImages, async (_input, ctx) => {
  for (const image of IMAGES) {
    for (let attempt = 1; ; attempt++) {
      try {
        // Copies only blobs the mirror lacks, so an unchanged image costs one manifest GET.
        await ctx.exec(["crane", "copy", "--platform", "linux/amd64", image, `${MIRROR}/${image}`], { idleTimeoutSeconds: 120 });
        break;
      } catch (e) {
        // Non-fatal: a stale mirror tag still builds, and a missing one falls back upstream.
        ctx.log("mirroring failed", { image, attempt, error: String(e) });
        if (attempt >= TRIES) break;
      }
    }
  }
});
