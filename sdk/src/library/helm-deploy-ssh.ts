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
import type { Configurable } from "../core/config.ts";
import { resolveConfigRefs } from "../core/config-refs.ts";
import { implement, type Implementation } from "../core/handlers.ts";
import { step, type LiteralName, type Step } from "../core/steps.ts";
import type { Simplify } from "../core/types.ts";
import { httpHealthCheck, type HttpHealthCheckOptions } from "./http-health.ts";
import { BuiltImage, HelmRelease, PushedImage, Source } from "./models.ts";
import { shq, sshSession } from "./ssh.ts";
import { helmDeployOverSshTarget, type HelmDeploySshTargetDef, type HelmTargetDeployInput } from "./helm-deploy.ts";

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
export type HelmDeployOverSshInput<
  Im extends Record<string, string> = DefaultImages,
  R extends boolean = false,
  Rf extends Record<string, string> = {},
> = Simplify<
  { readonly source: typeof Source } & { readonly [K in keyof Im]: ImageModel<R> } & { readonly [K in keyof Rf]: typeof PushedImage }
>;

export interface HelmDeployOverSshDef<
  N extends string,
  K extends string,
  Im extends Record<string, string> = DefaultImages,
  R extends boolean = false,
  Rf extends Record<string, string> = {},
> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /** SSH target, `user@host`. The user needs passwordless sudo for nerdctl, and helm/kubectl with a kubeconfig. */
  host: Configurable<string>;
  /** Private key for `host`. */
  sshKey: Secret<K>;
  /** Helm release name. */
  release: Configurable<string>;
  namespace: Configurable<string>;
  /** Chart directory inside the source tree, e.g. `helm`. */
  chart: string;
  /** Values files, relative to the chart directory (`-f`, in order). */
  values?: Configurable<readonly string[]>;
  /**
   * Chart-specific `--set` flags (e.g. `{ "ingress.enabled": "false" }`). The image flags are added by
   * the step and always win: `<prefix>.repository`, `<prefix>.tag`, `<prefix>.pullPolicy`.
   */
  set?: Configurable<Readonly<Record<string, string>>>;
  /**
   * The images to deploy: step input key → the chart's values prefix for that image. Each input is a
   * `BuiltImage` (or `PushedImage` with `fromRegistry`). Default `{ image: "image" }`; several images
   * need a mapper, e.g. `{ server: "image", web: "web.image" }`. `{}` deploys the chart alone.
   */
  images?: Im & { readonly source?: never };
  /**
   * The images are `PushedImage`s from a registry (`imageBuild` with `push`): pulled on the host
   * first, then `pullPolicy=IfNotPresent`. Default: `BuiltImage` tarballs, imported on the host, then
   * `pullPolicy=Never`.
   */
  fromRegistry?: R & (boolean extends R ? "❌ fromRegistry must be a literal true or false (use `as const` on a shared config object)" : unknown);
  /**
   * Registry images the chart only references (not run by the release's own pods, e.g. an image the
   * app starts Jobs with): step input key → values prefix. Each input is a `PushedImage`; the step sets
   * `<prefix>.repository`, `<prefix>.tag` and `<prefix>.digest` and does not pull it on the host. Works
   * next to tarball `images`.
   */
  imageRefs?: Rf & { readonly source?: never };
  /** Rollouts to wait for, in order: `deploy/<name>` for a bare name. Default: the release name, or none without images. */
  deployments?: readonly string[];
  /** Per rollout (`kubectl rollout status --timeout`). Default 120. */
  rolloutTimeoutSeconds?: number;
  /** After the rollouts, GET this URL until it returns 2xx (`httpHealthCheck`: 5 × 5 s unless given), or fail the step. */
  healthUrl?: Configurable<string> | ({ url: Configurable<string> } & Pick<HttpHealthCheckOptions, "attempts" | "delaySeconds">);
  timeoutSeconds?: number;
}

export interface HelmImageFlags {
  prefix: string;
  repository: string;
  tag: string;
  pullPolicy: "Never" | "IfNotPresent";
  /** Pins the image: `<prefix>.digest` (the chart renders `repository:tag@digest`). */
  digest?: string;
}

/** A registry image the chart only references (`imageRefs`). */
export interface HelmImageRefFlags {
  prefix: string;
  repository: string;
  tag: string;
  digest: string;
}

export interface HelmFlagOptions {
  values: readonly string[];
  set: Readonly<Record<string, string>>;
  images: readonly HelmImageFlags[];
  imageRefs?: readonly HelmImageRefFlags[];
}

/** `helm upgrade` flags: values files, `--set`, then the image flags (which win). */
export function helmFlags(o: HelmFlagOptions): string[] {
  // Chart-relative values paths (`-f values-beta.yaml`) resolve because helm runs from inside the
  // copied chart dir. Extra flags come before the image flags, so the image overrides win.
  // --set-string: a tag like `2026.10` or `1e5` would otherwise be parsed as a number.
  const flags = [
    ...o.values.flatMap((v) => ["-f", v]),
    ...Object.entries(o.set).flatMap(([k, v]) => ["--set", `${k}=${v}`]),
    ...o.images.flatMap((i) => [
      "--set", `${i.prefix}.repository=${i.repository}`,
      "--set", `${i.prefix}.tag=${i.tag}`,
      "--set", `${i.prefix}.pullPolicy=${i.pullPolicy}`,
      ...(i.digest ? ["--set-string", `${i.prefix}.digest=${i.digest}`] : []),
    ]),
    ...(o.imageRefs ?? []).flatMap((i) => [
      "--set-string", `${i.prefix}.repository=${i.repository}`,
      "--set-string", `${i.prefix}.tag=${i.tag}`,
      "--set-string", `${i.prefix}.digest=${i.digest}`,
    ]),
  ];
  return flags;
}

/** Remote `helm upgrade --install`, run from inside the copied chart directory. */
export function helmUpgradeCommand(o: HelmFlagOptions & { remoteChart: string; release: string; namespace: string }): string {
  const flags = helmFlags(o);
  return [
    `cd ${shq(o.remoteChart)}`,
    ["helm", "upgrade", "--install", o.release, ".", "-n", o.namespace, ...flags].map(shq).join(" "),
    `cd / && rm -rf ${shq(o.remoteChart)}`,
  ].join(" && ");
}

/** One image to get onto the host before the upgrade. */
export interface HostImage {
  repository: string;
  tag: string;
  digest?: string;
  /** An image tarball to stream in; without one the image is pulled from its registry. */
  tar?: string;
}

/**
 * Over SSH: get each image into the host's k3s containerd, copy the chart (with any generated values
 * files in it), `helm upgrade --install`, then wait for each rollout.
 */
export async function deployOverSsh(
  ctx: Ctx,
  o: {
    host: string;
    key: string;
    chartDir: string;
    release: string;
    namespace: string;
    hostImages: readonly HostImage[];
    flags: HelmFlagOptions;
    deployments: readonly string[];
    rolloutTimeoutSeconds: number;
  },
): Promise<void> {
  const ssh = await sshSession(ctx, { host: o.host, key: o.key });
  for (const image of o.hostImages) {
    if (image.tar) {
      ctx.log("importing image", { image: `${image.repository}:${image.tag}`, host: o.host });
      // Streamed over ssh's stdin (ctx.exec has none, hence the shell redirect).
      await ctx.sh(`${ssh.shell(K3S_IMAGE_IMPORT)} < ${shq(image.tar)}`);
    } else {
      const ref = image.digest ? `${image.repository}@${image.digest}` : `${image.repository}:${image.tag}`;
      ctx.log("pulling image", { image: ref, host: o.host });
      await ssh.run(`${K3S_IMAGE_PULL} ${shq(ref)}`);
    }
  }
  const remoteChart = `/tmp/${o.release}-helm-${o.namespace}`;
  ctx.log("helm upgrade", { release: o.release, namespace: o.namespace, host: o.host });
  await ssh.run(`rm -rf ${shq(remoteChart)}`);
  await ssh.copy(o.chartDir, remoteChart);
  await ssh.run(helmUpgradeCommand({ ...o.flags, remoteChart, release: o.release, namespace: o.namespace }));
  for (const d of o.deployments) {
    const target = d.includes("/") ? d : `deploy/${d}`;
    ctx.log("waiting for rollout", { deployment: target });
    await ssh.run(`kubectl -n ${shq(o.namespace)} rollout status ${shq(target)} --timeout=${o.rolloutTimeoutSeconds}s`);
  }
}

/** The chart directory inside a fetched source tree, or an error naming it. */
export function chartDirectory(tree: string, chart: string): string {
  const dir = join(tree, chart);
  if (!existsSync(dir) || !statSync(dir).isDirectory()) throw new Error(`chart directory '${chart}' not found in the source tree`);
  return dir;
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
  const Rf extends Record<string, string> = {},
>(def: HelmDeploySshTargetDef<N, K, Im, Rf>): Implementation<Step<N, HelmTargetDeployInput<Im, Rf>, typeof HelmRelease, K>>;
export function helmDeployOverSsh<
  const N extends string,
  const K extends string,
  const Im extends Record<string, string> = DefaultImages,
  const R extends boolean = false,
  const Rf extends Record<string, string> = {},
>(def: HelmDeployOverSshDef<N, K, Im, R, Rf>): Implementation<Step<N, HelmDeployOverSshInput<Im, R, Rf>, typeof HelmRelease, K>>;
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function helmDeployOverSsh(def: any): any {
  return "target" in def ? helmDeployOverSshTarget(def) : helmDeployOverSshLiteral(def);
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
function helmDeployOverSshLiteral(def: HelmDeployOverSshDef<string, string, any, boolean, any>): Implementation {
  const images: Record<string, string> = def.images ?? { image: "image" };
  const imageEntries = Object.entries(images);
  const fromRegistry = def.fromRegistry === true;
  const rolloutTimeout = def.rolloutTimeoutSeconds ?? DEFAULT_ROLLOUT_TIMEOUT_SECONDS;
  if ("source" in images) throw new Error(`helmDeployOverSsh '${def.name}': 'source' can't be an image input`);
  if (def.deployments?.length === 0) throw new Error(`helmDeployOverSsh '${def.name}': deployments must not be empty`);
  const imageRefs: Record<string, string> = def.imageRefs ?? {};
  const refEntries = Object.entries(imageRefs);
  if ("source" in imageRefs) throw new Error(`helmDeployOverSsh '${def.name}': 'source' can't be an image input`);
  for (const [key] of refEntries) if (key in images) throw new Error(`helmDeployOverSsh '${def.name}': '${key}' is both in images and imageRefs`);
  const imageModel = fromRegistry ? PushedImage : BuiltImage;
  const input = {
    source: Source,
    ...Object.fromEntries(imageEntries.map(([key]) => [key, imageModel])),
    ...Object.fromEntries(refEntries.map(([key]) => [key, PushedImage])),
  };
  const isDefaultImages = imageEntries.length === 1 && images.image === "image";
  const configurable = { host: def.host, release: def.release, namespace: def.namespace, values: def.values ?? [], set: def.set ?? {}, healthUrl: def.healthUrl };

  const contract = step({
    name: def.name,
    description: def.description,
    kind: "helmDeployOverSsh",
    image: HELM_DEPLOY_IMAGE,
    input,
    output: HelmRelease,
    secrets: [def.sshKey],
    timeoutSeconds: def.timeoutSeconds,
    settings: {
      host: def.host,
      sshKey: def.sshKey.name,
      release: def.release,
      namespace: def.namespace,
      chart: def.chart,
      values: configurable.values,
      set: configurable.set,
      ...(isDefaultImages ? {} : { images: { ...images } }),
      ...(fromRegistry ? { fromRegistry: true } : {}),
      ...(refEntries.length > 0 ? { imageRefs: { ...imageRefs } } : {}),
      ...(def.deployments ? { deployments: [...def.deployments] } : {}),
      ...(def.rolloutTimeoutSeconds !== undefined ? { rolloutTimeoutSeconds: def.rolloutTimeoutSeconds } : {}),
      ...(def.healthUrl !== undefined ? { healthUrl: def.healthUrl } : {}),
    },
  });
  return implement(contract, async (stepInput, ctx: Ctx) => {
    const input = stepInput as unknown as Record<string, { repository: string; tag: string; digest: string; tar?: FileRef }> & { source: { tree: FileRef } };
    const d = resolveConfigRefs(configurable, ctx.config ?? {}) as {
      host: string; release: string; namespace: string; values: string[]; set: Record<string, string>;
      healthUrl?: string | ({ url: string } & Pick<HttpHealthCheckOptions, "attempts" | "delaySeconds">);
    };
    const chartDir = chartDirectory(await ctx.files.fetch(input.source.tree), def.chart);
    const hostImages: HostImage[] = [];
    const flags: HelmImageFlags[] = [];
    for (const [key, prefix] of imageEntries) {
      const image = input[key]!;
      hostImages.push({ repository: image.repository, tag: image.tag, ...(fromRegistry ? {} : { tar: await ctx.files.fetch(image.tar!) }) });
      flags.push({ prefix, repository: image.repository, tag: image.tag, pullPolicy: fromRegistry ? "IfNotPresent" : "Never" });
    }
    const refs: HelmImageRefFlags[] = refEntries.map(([key, prefix]) => ({ prefix, repository: input[key]!.repository, tag: input[key]!.tag, digest: input[key]!.digest }));
    await deployOverSsh(ctx, {
      host: d.host, key: ctx.secrets[def.sshKey.name]!, chartDir, release: d.release, namespace: d.namespace, hostImages,
      flags: { values: d.values, set: d.set, images: flags, imageRefs: refs },
      deployments: def.deployments ?? (imageEntries.length ? [d.release] : []), rolloutTimeoutSeconds: rolloutTimeout,
    });
    const health = typeof d.healthUrl === "string" ? { url: d.healthUrl } : d.healthUrl;
    if (health) await httpHealthCheck(ctx, health.url, { attempts: health.attempts, delaySeconds: health.delaySeconds });
    return { release: d.release, namespace: d.namespace, ...(flags[0] ? { image: `${flags[0].repository}:${flags[0].tag}` } : {}) };
  }) as never;
}
