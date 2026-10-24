import { config, helmTarget, secret } from "@olivervea/pipelines";
import { base, steps, z } from "../fixtures2";

const KEY = secret("SSH_PRIVATE_KEY", "ssh key");
const TARGET = config("target", "Where to deploy.", z.object({ prod: helmTarget(), name: z.string() }));

// every case is required
// @ts-expect-error -- expect: Property 'inCluster' is missing
base().select(TARGET.prod, {
  ssh: (p) => p,
});

// a case that doesn't exist
base().select(TARGET.prod, {
  ssh: (p) => p,
  inCluster: (p) => p,
  // @ts-expect-error -- expect: 'docker' is not a case of this select
  docker: (p) => p,
});

// select needs a discriminated union
// @ts-expect-error -- expect: select needs a reference to a discriminated union
base().select(TARGET.name, {});

// a case's reference is narrowed to its own variant
base().select(TARGET.prod, {
  ssh: (p, prod) => {
    // @ts-expect-error -- expect: is not assignable to type 'Configurable<InClusterTargetValue>'
    steps.helmDeployInCluster({ name: "deploy", description: "", target: prod, chart: "helm" });
    return p;
  },
  inCluster: (p, prod) => {
    // @ts-expect-error -- expect: No overload matches this call
    steps.helmDeployOverSsh({ name: "deploy", description: "", target: prod, sshKey: KEY, chart: "helm" });
    return p;
  },
});
