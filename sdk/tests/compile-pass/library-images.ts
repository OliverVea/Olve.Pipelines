import { secret, type Infer, type BuiltImage, type PushedImage } from "@olivervea/pipelines";
import { base, steps, checkout } from "../fixtures2";

const KEY = secret("SSH_PRIVATE_KEY", "key");
const server = steps.imageBuild({ name: "build-server", description: "", repository: "server" });
const web = steps.imageBuild({ name: "build-web", description: "", repository: "web", context: "src/frontend" });
const figures = steps.imageBuild({ name: "build-figures", description: "", repository: "registry.example/figures", context: "figures", push: true, contentTag: true });

// Several images: one input per key, picked by a mapper (two BuiltImage producers).
const deploy = steps.helmDeployOverSsh({
  name: "deploy", description: "", host: "u@h", sshKey: KEY, release: "app", namespace: "ns", chart: "helm",
  images: { server: "image", web: "web.image" }, deployments: ["app-server", "app-web"],
});
// Registry images are a distinct model, so they are matched without a mapper next to tarball images.
const deployFigures = steps.helmDeployOverSsh({
  name: "deploy-figures", description: "", host: "u@h", sshKey: KEY, release: "figures", namespace: "ns", chart: "figures/helm", fromRegistry: true,
});

type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;
const _webInput: Equal<keyof typeof deploy.input, "source" | "server" | "web"> = true;
const _figuresIn: Equal<(typeof deployFigures.input)["image"], typeof PushedImage> = true;
const _figuresOut: Equal<Infer<NonNullable<typeof figures.output>>, Infer<typeof PushedImage>> = true;
const _serverOut: Equal<Infer<NonNullable<typeof server.output>>, Infer<typeof BuiltImage>> = true;

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(server, web, figures)
  .stage([deploy, from => ({ source: from.checkout, server: from["build-server"], web: from["build-web"] })], deployFigures);

// A registry image the chart only references, next to a tarball image: distinct input models.
const GHCR = secret("GHCR_TOKEN", "token");
const harness = steps.imageBuild({ name: "publish-harness", description: "", repository: "ghcr.io/o/harness", context: "harness/base", push: true, contentTag: true, registryAuth: { username: "o", password: GHCR } });
const deployWithRefs = steps.helmDeployOverSsh({
  name: "deploy-refs", description: "", host: "u@h", sshKey: KEY, release: "refs", namespace: "ns", chart: "helm", imageRefs: { harness: "harness.image" },
});
const _refsIn: Equal<keyof typeof deployWithRefs.input, "source" | "image" | "harness"> = true;
const _refsModel: Equal<(typeof deployWithRefs.input)["harness"], typeof PushedImage> = true;
const _harnessSecrets: Equal<(typeof harness.secrets)[number]["name"], "GHCR_TOKEN"> = true;

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(server, harness)
  .stage(deployWithRefs);
