import { steps } from "../fixtures2";

steps.imageBuild({
  name: "image",
  // run-dependent config is a function of the step's inputs
  // @ts-expect-error -- expect: Property 'version' does not exist
  tag: ({ source }) => `olve-pipelines:${source.version}`,
});
