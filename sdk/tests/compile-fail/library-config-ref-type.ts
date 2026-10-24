import { config, helmTarget, secret } from "@olivervea/pipelines";
import { steps, z } from "../fixtures2";

const KEY = secret("SSH_PRIVATE_KEY", "ssh key");
const TARGET = config("target", "Where to deploy.", z.object({ port: z.number(), host: z.string().optional(), beta: helmTarget() }));

steps.helmDeployOverSsh({
  name: "deploy", description: "", sshKey: KEY, release: "a", namespace: "n", chart: "helm",
  // a reference must have the field's type
  // @ts-expect-error -- expect: Type 'ConfigRef<number>' is not assignable to type 'Configurable<string>'
  host: TARGET.port,
});

steps.helmDeployOverSsh({
  name: "deploy", description: "", sshKey: KEY, release: "a", namespace: "n", chart: "helm",
  // an optional field may be undefined
  // @ts-expect-error -- expect: is not assignable to type 'Configurable<string>'
  host: TARGET.host,
});

steps.imageBuild({
  name: "build", description: "",
  // a field the entry doesn't have
  // @ts-expect-error -- expect: Property 'repository' does not exist
  repository: TARGET.repository,
});

steps.helmDeploy({
  name: "deploy", description: "", chart: "helm",
  // the target must be a helmTarget() value
  // @ts-expect-error -- expect: is not assignable to type 'Configurable<HelmTargetValue>'
  target: TARGET.port,
});
