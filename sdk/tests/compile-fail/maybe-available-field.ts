import { base, step, checkout, build, test, deployBeta, deployProd, z } from "../fixtures2";

// A field of a maybe-available output is maybe-undefined too.
const notify = step({ name: "notify", image: "alpine", input: { url: z.string() } });
base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(deployProd))
  // @ts-expect-error -- expect: Type 'Ref<string | undefined>' is not assignable to type 'Mappable<string>'
  .stage([notify, from => ({ url: from["deploy-prod"].url })]);
