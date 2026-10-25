import { config, HelmRelease, implement, resolveConfigRefs, serviceUrl, step, z, type Configurable, type LiteralName } from "@olivervea/pipelines";
import { testHandler } from "@olivervea/pipelines/testing";
import { base, steps, checkout } from "../fixtures2";

// The reusable step from docs/setup/pipeline-reference.md "Reusable steps".
export function smokeTest<const N extends string>(def: { name: LiteralName<N>; description: string; path: Configurable<string> }) {
  return implement(
    step({
      name: def.name,
      description: def.description,
      kind: "smokeTest",
      image: "alpine:3.22",
      input: { release: HelmRelease },
      settings: { path: def.path },
    }),
    async ({ release }, ctx) => {
      const path = resolveConfigRefs(def.path, ctx.config);
      await ctx.http.expectOk(`${release.url ?? serviceUrl(release)}${path}`);
    },
  );
}

const SITE = config("site", "", z.object({ healthPath: z.string() }));
const deploy = steps.helmDeployOverSsh({ name: "deploy", description: "", host: "u@h", sshKey: { kind: "secret", name: "K", description: "" } as never, release: "a", namespace: "n", chart: "helm", images: {} });
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(deploy)
  .stage(smokeTest({ name: "smoke", description: "", path: SITE.healthPath }));
void testHandler(smokeTest({ name: "smoke", description: "", path: "/health" }), { release: { release: "a", namespace: "n" } });
