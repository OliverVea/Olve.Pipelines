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
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(build)
  .stage([
    deployTo,
    i => ({
      artifact: {                      // rebuild a model field-by-field from refs (FileRefs only via refs)
        version: i.build.version,
        commit: i.build.commit,
        image: i.build.image,
        chart: i.build.chart,
      },
      target: { env: i.input.deployTo, url: "https://pipelines.ovea.pro", replicas: 2 },
      commit: { repo: i.input.repo, sha: i.checkout.sha },
    }),
  ]);
