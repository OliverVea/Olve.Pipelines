// Trivial stand-in for `describe`: evaluates a zod model and prints a manifest between sentinels.
import { z } from "zod";
const DeployRequest = z.object({ repo: z.string(), sha: z.string().length(40), deployTo: z.enum(["beta", "prod"]) });
const manifest = {
  apiVersion: "2.0",
  name: "Olve.Pipelines",
  input: "DeployRequest",
  models: { DeployRequest: z.toJSONSchema(DeployRequest) },
  stages: [{ steps: [{ name: "build", image: "alpine:latest", output: "BuildArtifact" }] }],
};
console.log("---OLVE-MANIFEST-BEGIN---");
console.log(JSON.stringify(manifest));
console.log("---OLVE-MANIFEST-END---");
