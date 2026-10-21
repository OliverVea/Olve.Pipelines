import { base, step, checkout, build, z, M } from "../fixtures2";

// A mapper may build objects out of refs and literals, at any depth.
const deployTo = step({
  name: "deploy-to",
  image: "alpine",
  input: {
    artifact: M.BuildArtifact,
    target: z.object({ env: z.enum(["beta", "prod"]), url: z.string(), replicas: z.number() }),
    commit: z.object({ repo: z.string(), sha: z.string() }),
  },
});
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build)
  .stage([
    deployTo,
    from => ({
      artifact: {                      // rebuild a model field-by-field from refs (FileRefs only via refs)
        version: from.build.version,
        commit: from.build.commit,
        image: from.build.image,
        chart: from.build.chart,
      },
      target: { env: from.input.deployTo, url: "https://pipelines.ovea.pro", replicas: 2 },
      commit: { repo: from.input.repo, sha: from.checkout.sha },
    }),
  ]);
