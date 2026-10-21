import { base, step, checkout, build, test, deployBeta, approveProd, deploy, z } from "../fixtures2";

// A field of a maybe-available output is maybe-undefined too.
const notify = step({ name: "notify", image: "alpine", input: { url: z.string() } });
base()
  .stage([checkout, i => ({ repo: i.input.repo, sha: i.input.sha })])
  .stage(build, test)
  .stage(deployBeta)
  .when({ deployTo: "prod" }, p => p.stage(approveProd).stage(deploy))
  // @ts-expect-error -- expect: Type 'Ref<string | undefined>' is not assignable to type 'Mappable<string>'
  .stage([notify, i => ({ url: i.deploy.url })]);
