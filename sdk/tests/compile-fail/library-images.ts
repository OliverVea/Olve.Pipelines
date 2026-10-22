import { secret } from "@olivervea/pipelines";
import { base, steps, checkout } from "../fixtures2";

const KEY = secret("SSH_PRIVATE_KEY", "key");
const server = steps.imageBuild({ name: "build-server", description: "", repository: "server" });
const web = steps.imageBuild({ name: "build-web", description: "", repository: "web" });
const deploy = steps.helmDeployOverSsh({
  name: "deploy", description: "", host: "u@h", sshKey: KEY, release: "app", namespace: "ns", chart: "helm",
  images: { server: "image", web: "web.image" },
});

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(server, web)
  // @ts-expect-error -- expect: Property 'web' is missing
  .stage([deploy, from => ({ source: from.checkout, server: from["build-server"] })]);

// @ts-expect-error -- expect: is not assignable to type 'undefined'
steps.helmDeployOverSsh({ name: "d", description: "", host: "u@h", sshKey: KEY, release: "a", namespace: "n", chart: "helm", images: { source: "image" } });

// @ts-expect-error -- expect: No overload matches this call
steps.imageBuild({ name: "b", description: "", repository: "registry.example/x", contentTag: true });
