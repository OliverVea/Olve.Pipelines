import { steps, Source } from "../fixtures2";

steps.script({
  name: "tag",
  image: "alpine",
  script: "tag.sh",
  input: { source: Source },
  // run-dependent config is a function of the step's typed inputs
  // @ts-expect-error -- expect: Property 'version' does not exist
  env: { TAG: ({ source }) => `olve-pipelines:${source.version}` },
});
