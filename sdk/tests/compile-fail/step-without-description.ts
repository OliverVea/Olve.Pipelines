import { step, steps } from "@olivervea/pipelines";

// @ts-expect-error -- expect: Property 'description' is missing
step({ name: "s", image: "alpine" });
// @ts-expect-error -- expect: Property 'description' is missing
steps.gitHubSource({ name: "source" });
