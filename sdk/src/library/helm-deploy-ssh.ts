// steps.helmDeployOverSsh — get built images onto a k3s host and helm-upgrade a release there, over
// SSH; optionally verify an HTTP health endpoint afterwards.
//
// Ported from v1's olve-lib.sh (olve_ssh_host, olve_image_import, olve_helm_deploy) and
// deploy-beta.sh (rollout wait + health loop); the footguns are kept as comments below.
import { existsSync, statSync } from "node:fs";
import { join } from "node:path";
import type { Ctx } from "../ctx/types.ts";
import type { FileRef } from "../core/models.ts";
import type { Secret } from "../core/secrets.ts";
import { makeStep, type LibraryStep, type LiteralName, type Step } from "../core/steps.ts";
import type { Simplify } from "../core/types.ts";
import { httpHealthCheck, type HttpHealthCheckOptions } from "./http-health.ts";
import { BuiltImage, HelmRelease, PushedImage, Source } from "./models.ts";
import { shq, sshSession } from "./ssh.ts";

export { shq } from "./ssh.ts";

/** Alpine: `apk add openssh-client` at run time gives ssh/scp (the health check runs in the harness). */
export const HELM_DEPLOY_IMAGE = "alpine:3.22";

/**
 * Footgun: import via the k3s containerd socket, NOT the default one — kubelet only sees images in
 * the k3s instance's `k8s.io` namespace (otherwise pods fail with ErrImageNeverPull).
 */
export const K3S_IMAGE_IMPORT = "sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load";
/** Registry images are pulled the same way before the rollout, so the rollout doesn't wait on the pull. */
export const K3S_IMAGE_PULL = "sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io pull -q";

export const DEFAULT_ROLLOUT_TIMEOUT_SECONDS = 120;

type DefaultImages = { readonly image: "image" };
type ImageModel<R extends boolean> = R extends true ? typeof PushedImage : typeof BuiltImage;
export type HelmDeployOverSshInput<Im extends Record<string, string> = DefaultImages, R extends boolean = false> = Simplify<
  { readonly source: typeof Source } & { readonly [K in keyof Im]: ImageModel<R> }
>;

export interface HelmDeployOverSshDef<N extends string, K extends string, Im extends Record<string, string> = DefaultImages, R extends boolean = false> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /** SSH target, `user@host`. The user needs passwordless sudo for nerdctl, and helm/kubectl with a kubeconfig. */
  host: string;
  /** Private key for `host`. */
  sshKey: Secret<K>;
  /** Helm release name. */
  release: string;
  namespace: string;
  /** Chart directory inside the source tree, e.g. `helm`. */
  chart: string;
  /** Values files, relative to the chart directory (`-f`, in order). */
  values?: readonly string[];
  /**
   * Chart-specific `--set` flags (e.g. `{ "ingress.enabled": "false" }`). The image flags are added by
   * the step and always win: `<prefix>.repository`, `<prefix>.tag`, `<prefix>.pullPolicy`.
   */
  set?: Readonly<Record<string, string>>;
  /**
   * The images to deploy: step input key → the chart's values prefix for that image. Each input is a
   * `BuiltImage` (or `PushedImage` with `fromRegistry`). Default `{ image: "image" }`; several images
   * need a mapper, e.g. `{ server: "image", web: "web.image" }`.
   */
  images?: Im & { readonly source?: never };
  /**
   * The images are `PushedImage`s from a registry (`imageBuild` with `push`): pulled on the host
   * first, then `pullPolicy=IfNotPresent`. Default: `BuiltImage` tarballs, imported on the host, then
   * `pullPolicy=Never`.
   */
  fromRegistry?: R;
  /** Rollouts to wait for, in order: `deploy/<name>` for a bare name. Default: the release name. */
  deployments?: readonly string[];
  /** Per rollout (`kubectl rollout status --timeout`). Default 120. */
  rolloutTimeoutSeconds?: number;
  /** After the rollouts, GET this URL until it returns 2xx (`httpHealthCheck`: 5 × 5 s unless given), or fail the step. */
  healthUrl?: string | ({ url: string } & Pick<HttpHealthCheckOptions, "attempts" | "delaySeconds">);
  timeoutSeconds?: number;
}

export interface HelmImageFlags {
  prefix: string;
  repository: string;
  tag: string;
  pullPolicy: "Never" | "IfNotPresent";
}

/** Remote `helm upgrade --install`, run from inside the copied chart directory. */
export function helmUpgradeCommand(o: {
  remoteChart: string;
  release: string;
  namespace: string;
  values: readonly string[];
  set: Readonly<Record<string, string>>;
  images: readonly HelmImageFlags[];
}): string {
  // Chart-relative values paths (`-f values-beta.yaml`) resolve because helm runs from inside the
  // copied chart dir. Extra flags come before the image flags, so the image overrides win.
  const flags = [
    ...o.values.flatMap((v) => ["-f", v]),
    ...Object.entries(o.set).flatMap(([k, v]) => ["--set", `${k}=${v}`]),
    ...o.images.flatMap((i) => [
      "--set", `${i.prefix}.repository=${i.repository}`,
      "--set", `${i.prefix}.tag=${i.tag}`,
      "--set", `${i.prefix}.pullPolicy=${i.pullPolicy}`,
    ]),
  ];
  return [
    `cd ${shq(o.remoteChart)}`,
    ["helm", "upgrade", "--install", o.release, ".", "-n", o.namespace, ...flags].map(shq).join(" "),
    `cd / && rm -rf ${shq(o.remoteChart)}`,
  ].join(" && ");
}

/**
 * Deploys images with the Helm chart at `<source>/<chart>` on a k3s host over SSH: get each image
 * into the host's k3s containerd (stream a tarball in, or pull from the registry), copy the chart,
 * `helm upgrade --install`, wait for each rollout, then (with `healthUrl`) poll the health endpoint.
 */
export function helmDeployOverSsh<
  const N extends string,
  const K extends string,
  const Im extends Record<string, string> = DefaultImages,
  const R extends boolean = false,
>(def: HelmDeployOverSshDef<N, K, Im, R>): Step<N, HelmDeployOverSshInput<Im, R>, typeof HelmRelease, K> & LibraryStep {
  const values = def.values ?? [];
  const set = def.set ?? {};
  const images: Record<string, string> = def.images ?? { image: "image" };
  const imageEntries = Object.entries(images);
  const fromRegistry = def.fromRegistry === true;
  const deployments = def.deployments ?? [def.release];
  const rolloutTimeout = def.rolloutTimeoutSeconds ?? DEFAULT_ROLLOUT_TIMEOUT_SECONDS;
  const health = typeof def.healthUrl === "string" ? { url: def.healthUrl } : def.healthUrl;
  if (imageEntries.length === 0) throw new Error(`helmDeployOverSsh '${def.name}': images must not be empty`);
  if ("source" in images) throw new Error(`helmDeployOverSsh '${def.name}': 'source' can't be an image input`);
  if (deployments.length === 0) throw new Error(`helmDeployOverSsh '${def.name}': deployments must not be empty`);
  const imageModel = fromRegistry ? PushedImage : BuiltImage;
  const input = { source: Source, ...Object.fromEntries(imageEntries.map(([key]) => [key, imageModel])) };
  const isDefaultImages = imageEntries.length === 1 && images.image === "image";

  return makeStep(
    "helmDeployOverSsh",
    { name: def.name, description: def.description, image: HELM_DEPLOY_IMAGE, input, output: HelmRelease, secrets: [def.sshKey], timeoutSeconds: def.timeoutSeconds },
    {
      manifestConfig: () => ({
        host: def.host,
        sshKey: def.sshKey.name,
        release: def.release,
        namespace: def.namespace,
        chart: def.chart,
        values: [...values],
        set: { ...set },
        ...(isDefaultImages ? {} : { images: { ...images } }),
        ...(fromRegistry ? { fromRegistry: true } : {}),
        ...(def.deployments ? { deployments: [...def.deployments] } : {}),
        ...(def.rolloutTimeoutSeconds !== undefined ? { rolloutTimeoutSeconds: def.rolloutTimeoutSeconds } : {}),
        ...(def.healthUrl !== undefined ? { healthUrl: typeof def.healthUrl === "string" ? def.healthUrl : { ...def.healthUrl } } : {}),
      }),
      async run(input: Record<string, { repository: string; tag: string; tar?: FileRef }> & { source: { tree: FileRef } }, ctx: Ctx) {
        const tree = await ctx.files.fetch(input.source.tree);
        const chartDir = join(tree, def.chart);
        if (!existsSync(chartDir) || !statSync(chartDir).isDirectory()) throw new Error(`chart directory '${def.chart}' not found in the source tree`);

        const ssh = await sshSession(ctx, { host: def.host, key: ctx.secrets[def.sshKey.name]! });
        const flags: HelmImageFlags[] = [];
        for (const [key, prefix] of imageEntries) {
          const image = input[key]!;
          const ref = `${image.repository}:${image.tag}`;
          if (fromRegistry) {
            ctx.log("pulling image", { image: ref, host: def.host });
            await ssh.run(`${K3S_IMAGE_PULL} ${shq(ref)}`);
          } else {
            const tar = await ctx.files.fetch(image.tar!);
            ctx.log("importing image", { image: ref, host: def.host });
            // Streamed over ssh's stdin (ctx.exec has none, hence the shell redirect).
            await ctx.sh(`${ssh.shell(K3S_IMAGE_IMPORT)} < ${shq(tar)}`);
          }
          flags.push({ prefix, repository: image.repository, tag: image.tag, pullPolicy: fromRegistry ? "IfNotPresent" : "Never" });
        }

        const remoteChart = `/tmp/${def.release}-helm-${def.namespace}`;
        ctx.log("helm upgrade", { release: def.release, namespace: def.namespace, images: flags.map((f) => `${f.repository}:${f.tag}`).join(" ") });
        await ssh.run(`rm -rf ${shq(remoteChart)}`);
        await ssh.copy(chartDir, remoteChart);
        await ssh.run(helmUpgradeCommand({ remoteChart, release: def.release, namespace: def.namespace, values, set, images: flags }));

        for (const d of deployments) {
          const target = d.includes("/") ? d : `deploy/${d}`;
          ctx.log("waiting for rollout", { deployment: target });
          await ssh.run(`kubectl -n ${shq(def.namespace)} rollout status ${shq(target)} --timeout=${rolloutTimeout}s`);
        }

        if (health) await httpHealthCheck(ctx, health.url, { attempts: health.attempts, delaySeconds: health.delaySeconds });
        return { release: def.release, namespace: def.namespace, image: `${flags[0]!.repository}:${flags[0]!.tag}` };
      },
    },
  ) as never;
}
