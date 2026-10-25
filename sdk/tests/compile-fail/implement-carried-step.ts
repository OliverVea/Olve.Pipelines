import { implement, steps } from "@olivervea/pipelines";

const source = steps.gitHubSource({ name: "source", description: "" });
// @ts-expect-error -- expect: this step already carries its implementation
implement(source, async ({ repo, sha }) => ({ repo, sha, tree: undefined as never }));
