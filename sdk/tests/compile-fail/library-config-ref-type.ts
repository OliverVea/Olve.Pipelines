import { config, helmTarget, secret } from "@olivervea/pipelines";
import { steps, z } from "../fixtures2";

const KEY = secret("SSH_PRIVATE_KEY", "ssh key");
const TARGET = config("target", "Where to deploy.", z.object({ port: z.number(), host: z.string().optional(), beta: helmTarget() }));

// a reference must have the field's type (one line: TS 5.9 reports overloads at the call, TS 7 at the field)
// @ts-expect-error -- expect: is not assignable to type 'Configurable<string>'
steps.helmDeployOverSsh({ name: "deploy", description: "", sshKey: KEY, release: "a", namespace: "n", chart: "helm", host: TARGET.port });

// an optional field may be undefined
// @ts-expect-error -- expect: is not assignable to type 'Configurable<string>'
steps.helmDeployOverSsh({ name: "deploy", description: "", sshKey: KEY, release: "a", namespace: "n", chart: "helm", host: TARGET.host });

steps.imageBuild({
  name: "build", description: "",
  // a field the entry doesn't have
  // @ts-expect-error -- expect: Property 'repository' does not exist
  repository: TARGET.repository,
});

steps.helmDeployInCluster({
  name: "deploy", description: "", chart: "helm",
  // the target must be an in-cluster target; the unnarrowed union is not one
  // @ts-expect-error -- expect: is not assignable to type 'Configurable<InClusterTargetValue>'
  target: TARGET.beta,
});
