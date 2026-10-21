import { base, steps, checkout } from "../fixtures2";

// Run-dependent library config is a function of the step's typed inputs (+ run info).
const image = steps.imageBuild({
  name: "image",
  dockerfile: "Dockerfile",
  tag: ({ source }, run) => `olve-pipelines:${run.version}-${source.sha.slice(0, 7)}`,
  buildArgs: { CONFIGURATION: "Release", COMMIT: ({ source }) => source.sha },
});
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(image);
