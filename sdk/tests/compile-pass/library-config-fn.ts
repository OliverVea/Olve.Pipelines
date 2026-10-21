import { base, steps, checkout, Source } from "../fixtures2";

// Run-dependent library config is a function of the step's typed inputs (+ run info).
const tag = steps.script({
  name: "tag",
  image: "alpine",
  script: "tag.sh",
  input: { source: Source },
  env: { STATIC: "x", TAG: ({ source }, run) => `olve-pipelines:${run.version}-${source.sha.slice(0, 7)}` },
});
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(tag);
