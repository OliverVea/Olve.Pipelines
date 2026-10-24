// Helm deploys to a target from config (`helmTarget()`): steps.helmDeployInCluster runs helm in the step
// pod against its own cluster; steps.helmDeployOverSsh({ target, … }) runs it on a k3s host. Pick one
// per target kind with `select`. Images come from a registry, pinned by digest.
import { existsSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { z } from "zod";
import type { Ctx } from "../ctx/types.ts";
import type { Configurable } from "../core/config.ts";
import { recordConfigRefs, resolveConfigRefs } from "../core/config-refs.ts";
import type { FileRef } from "../core/models.ts";
import { optional } from "../core/models.ts";
import type { Secret } from "../core/secrets.ts";
import { secretRef } from "../core/config.ts";
import { makeStep, type LibraryStep, type LiteralName, type Step, type StepKind } from "../core/steps.ts";
import type { Simplify } from "../core/types.ts";
import { serviceUrl } from "./checks.ts";
import { chartDirectory, deployOverSsh, DEFAULT_ROLLOUT_TIMEOUT_SECONDS, HELM_DEPLOY_IMAGE, helmFlags, type HelmImageFlags, type HelmImageRefFlags } from "./helm-deploy-ssh.ts";
import { httpHealthCheck } from "./http-health.ts";
import { HelmRelease, PushedImage, Source } from "./models.ts";

/** helm 3 on Alpine, for in-cluster deploys (the health check runs in the harness). */
export const HELM_IMAGE = "alpine/helm:3.16.2";

/** Where the step pod finds its ServiceAccount token (mounted only with the instance's step ServiceAccount). */
export const SERVICE_ACCOUNT_TOKEN = "/var/run/secrets/kubernetes.io/serviceaccount/token";

function targetVariants<const E extends z.ZodRawShape, const K extends string>(o: Omit<DeployTargetOptions<E, K>, "composeToken"> & { composeToken?: Secret<K> }) {
  const name = (d: string | undefined) => (d === undefined ? z.string() : z.string().default(d));
  const common = {
    namespace: name(o.namespace).describe("Namespace the release is installed into."),
    release: name(o.release).describe("Helm release name."),
    values: z.array(z.string()).default([]).describe("Values files, relative to the chart directory (-f, in order)."),
    set: z.record(z.string(), z.string()).default({}).describe("Extra --set flags."),
    ...(o.extend ?? ({} as E)),
  };
  const inCluster = z.object({ kind: z.literal("inCluster").describe("helm runs in the step pod against its own cluster (needs the instance's step ServiceAccount)."), ...common });
  const ssh = z.object({ kind: z.literal("ssh").describe("helm runs on a k3s host over SSH."), host: z.string().describe("user@host"), ...common });
  const compose = z.object({
    kind: z.literal("compose").describe("A Docker Compose deployer recreates a service with the image."),
    deployerUrl: z.string().describe("The deployer's base URL (POST <deployerUrl>/v1/deploy)."),
    service: z.string().describe("The compose service to recreate."),
    token: secretRef(o.composeToken ?? ({ kind: "secret", name: "DEPLOYER_TOKEN", description: "" } as Secret<K>)).describe("The deployer's bearer token."),
    healthUrl: z.string().optional().describe("Checked by the step after the deployer's own health check, when the step can reach it."),
    ...(o.extend ?? ({} as E)),
  });
  const none = z.object({ kind: z.literal("none").describe("Not deployed: the target's steps are left out.") });
  return { inCluster, ssh, compose, none };
}

export interface DeployTargetOptions<E extends z.ZodRawShape, K extends string> {
  /** Default namespace (helm targets). */
  namespace?: string;
  /** Default release name (helm targets). */
  release?: string;
  /** Fields added to every kind but `none` (e.g. per-target auth overrides). */
  extend?: E;
  /** The secret holding the compose deployer's token (`kind: compose`'s `token: { secret: … }`). */
  composeToken: Secret<K>;
  /** Adds `kind: none`, a target that isn't deployed (e.g. an optional beta). */
  optional?: boolean;
}

/**
 * The config schema of a deploy target, a union on `kind` to `select` on: `inCluster` and `ssh` (helm),
 * `compose` (a Docker Compose deployer), and with `optional: true` also `none` (not deployed).
 * `config("target", "…", z.object({ beta: deployTarget({ optional: true, composeToken: T }), prod: deployTarget({ composeToken: T }) }))`
 */
export function deployTarget<const K extends string, const E extends z.ZodRawShape = {}>(o: DeployTargetOptions<E, K> & { optional: true }): z.ZodDiscriminatedUnion<[
  ReturnType<typeof targetVariants<E, K>>["inCluster"], ReturnType<typeof targetVariants<E, K>>["ssh"], ReturnType<typeof targetVariants<E, K>>["compose"], ReturnType<typeof targetVariants<E, K>>["none"],
]>;
export function deployTarget<const K extends string, const E extends z.ZodRawShape = {}>(o: DeployTargetOptions<E, K>): z.ZodDiscriminatedUnion<[
  ReturnType<typeof targetVariants<E, K>>["inCluster"], ReturnType<typeof targetVariants<E, K>>["ssh"], ReturnType<typeof targetVariants<E, K>>["compose"],
]>;
export function deployTarget(o: DeployTargetOptions<z.ZodRawShape, string>): unknown {
  const v = targetVariants(o);
  return z.discriminatedUnion("kind", o.optional ? [v.inCluster, v.ssh, v.compose, v.none] : [v.inCluster, v.ssh, v.compose]);
}

/** A helm-only deploy target (`inCluster | ssh`). */
export function helmTarget<const E extends z.ZodRawShape = {}>(defaults: { namespace?: string; release?: string } = {}, extend?: E) {
  const v = targetVariants({ ...defaults, extend });
  return z.discriminatedUnion("kind", [v.inCluster, v.ssh]);
}

interface TargetCommon {
  namespace: string;
  release: string;
  values: readonly string[];
  set: Readonly<Record<string, string>>;
}
/** A resolved `kind: inCluster` target. */
export type InClusterTargetValue = TargetCommon & { kind: "inCluster" };
/** A resolved `kind: ssh` target. */
export type SshTargetValue = TargetCommon & { kind: "ssh"; host: string };
/** A resolved deploy target (the output of `helmTarget()`). */
export type HelmTargetValue = InClusterTargetValue | SshTargetValue;

type DefaultImages = { readonly image: "image" };
export type HelmTargetDeployInput<Im extends Record<string, string> = DefaultImages, Rf extends Record<string, string> = {}> = Simplify<
  { readonly source: typeof Source } & { readonly [K in keyof Im]: typeof PushedImage } & { readonly [K in keyof Rf]: ReturnType<typeof optional<typeof PushedImage>> }
>;

/** What both target deploys take (besides `target`). */
export interface HelmTargetDeployDef<N extends string, Im extends Record<string, string> = DefaultImages, Rf extends Record<string, string> = {}> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /** Chart directory inside the source tree, e.g. `helm`. */
  chart: string;
  /**
   * Values written as files and passed after the target's values files (`-f`, in order). Config
   * references are resolved first; undefined members are dropped, so `{ auth: target.auth }`
   * overrides only when set.
   */
  inlineValues?: readonly Readonly<Record<string, unknown>>[];
  /**
   * The images to deploy: step input key → the chart's values prefix. Each input is a `PushedImage`;
   * the step sets `<prefix>.repository`, `.tag`, `.digest` and `.pullPolicy=IfNotPresent`.
   */
  images?: Im & { readonly source?: never };
  /**
   * Registry images the chart only references: input key → values prefix. Each input is an
   * `optional(PushedImage)`; when its producer was skipped the values are left alone.
   */
  imageRefs?: Rf & { readonly source?: never };
  /** Rollout wait: per rollout over SSH, `helm --wait --timeout` in-cluster. Default 120. */
  rolloutTimeoutSeconds?: number;
  /** After the rollout, GET `http://<release>.<namespace>.svc.cluster.local<healthPath>` until it returns 2xx. */
  healthPath?: string;
  timeoutSeconds?: number;
}

export interface HelmDeployInClusterDef<N extends string, Im extends Record<string, string> = DefaultImages, Rf extends Record<string, string> = {}>
  extends HelmTargetDeployDef<N, Im, Rf> {
  /** An in-cluster target, usually a `select` case's reference (`(p, prod) => … target: prod`). */
  target: Configurable<InClusterTargetValue>;
}

export interface HelmDeploySshTargetDef<N extends string, K extends string, Im extends Record<string, string> = DefaultImages, Rf extends Record<string, string> = {}>
  extends HelmTargetDeployDef<N, Im, Rf> {
  /** An ssh target, usually a `select` case's reference. */
  target: Configurable<SshTargetValue>;
  /** Private key for the target's host. */
  sshKey: Secret<K>;
  /** Rollouts to wait for (`deploy/<name>` for a bare name). Default: the release name. */
  deployments?: readonly string[];
}

/**
 * Deploys registry images, pinned by digest, with the Helm chart at `<source>/<chart>` into the
 * cluster the step runs in: `helm upgrade --install --wait` in the step pod, with the instance's step
 * ServiceAccount token (`clusterAccess`). Then (with `healthPath`) polls the release's Service.
 */
export function helmDeployInCluster<
  const N extends string,
  const Im extends Record<string, string> = DefaultImages,
  const Rf extends Record<string, string> = {},
>(def: HelmDeployInClusterDef<N, Im, Rf>): Step<N, HelmTargetDeployInput<Im, Rf>, typeof HelmRelease, never> & LibraryStep {
  return targetDeploy("helmDeployInCluster", def as unknown as AnyTargetDef, undefined) as never;
}

/** The `target` form of `steps.helmDeployOverSsh`: an ssh target from config, registry images by digest. */
export function helmDeployOverSshTarget(def: HelmDeploySshTargetDef<string, string, Record<string, string>, Record<string, string>>): Step & LibraryStep {
  return targetDeploy("helmDeployOverSsh", def as unknown as AnyTargetDef, def.sshKey, def.deployments) as never;
}

interface AnyTargetDef {
  name: string;
  description: string;
  chart: string;
  target: unknown;
  inlineValues?: readonly Readonly<Record<string, unknown>>[];
  images?: Readonly<Record<string, string>>;
  imageRefs?: Readonly<Record<string, string>>;
  rolloutTimeoutSeconds?: number;
  healthPath?: string;
  timeoutSeconds?: number;
}

function targetDeploy(kind: StepKind, def: AnyTargetDef, sshKey: Secret | undefined, deployments?: readonly string[]): Step & LibraryStep {
  const images: Record<string, string> = { ...(def.images ?? { image: "image" }) };
  const imageEntries = Object.entries(images);
  const imageRefs: Record<string, string> = { ...(def.imageRefs ?? {}) };
  const refEntries = Object.entries(imageRefs);
  const rolloutTimeout = def.rolloutTimeoutSeconds ?? DEFAULT_ROLLOUT_TIMEOUT_SECONDS;
  if (imageEntries.length === 0) throw new Error(`${kind} '${def.name}': images must not be empty`);
  if ("source" in images || "source" in imageRefs) throw new Error(`${kind} '${def.name}': 'source' can't be an image input`);
  for (const [key] of refEntries) if (key in images) throw new Error(`${kind} '${def.name}': '${key}' is both in images and imageRefs`);
  if (deployments?.length === 0) throw new Error(`${kind} '${def.name}': deployments must not be empty`);
  if (def.healthPath !== undefined && !def.healthPath.startsWith("/")) throw new Error(`${kind} '${def.name}': healthPath must start with '/'`);
  const inCluster = kind === "helmDeployInCluster";
  const input = {
    source: Source,
    ...Object.fromEntries(imageEntries.map(([key]) => [key, PushedImage])),
    ...Object.fromEntries(refEntries.map(([key]) => [key, optional(PushedImage)])),
  };
  const configurable = { target: def.target, inlineValues: def.inlineValues ?? [] };

  return makeStep(
    kind,
    {
      name: def.name, description: def.description, image: inCluster ? HELM_IMAGE : HELM_DEPLOY_IMAGE, input, output: HelmRelease,
      secrets: sshKey ? [sshKey] : [], timeoutSeconds: def.timeoutSeconds, ...(inCluster ? { clusterAccess: true } : {}),
    },
    {
      manifestConfig: () => ({
        target: recordConfigRefs(def.target),
        ...(sshKey ? { sshKey: sshKey.name } : {}),
        chart: def.chart,
        ...(def.inlineValues?.length ? { inlineValues: recordConfigRefs(def.inlineValues) } : {}),
        images: { ...images },
        ...(refEntries.length > 0 ? { imageRefs: { ...imageRefs } } : {}),
        ...(deployments ? { deployments: [...deployments] } : {}),
        ...(def.rolloutTimeoutSeconds !== undefined ? { rolloutTimeoutSeconds: def.rolloutTimeoutSeconds } : {}),
        ...(def.healthPath !== undefined ? { healthPath: def.healthPath } : {}),
      }),
      async run(input: Record<string, { repository: string; tag: string; digest: string } | undefined> & { source: { tree: FileRef } }, ctx: Ctx) {
        const { target, inlineValues } = resolveConfigRefs(configurable, ctx.config ?? {}) as { target: HelmTargetValue; inlineValues: Record<string, unknown>[] };
        const want = inCluster ? "inCluster" : "ssh";
        if (target?.kind !== want) throw new Error(`${kind} '${def.name}': the target is kind '${String(target?.kind)}', not '${want}'`);
        const chartDir = chartDirectory(await ctx.files.fetch(input.source.tree), def.chart);

        const generated = inlineValues.map((values, i) => {
          const file = `olve-values-${i}.json`;
          writeFileSync(join(chartDir, file), JSON.stringify(values));
          return file;
        });
        const flags: HelmImageFlags[] = imageEntries.map(([key, prefix]) => {
          const image = input[key]!;
          return { prefix, repository: image.repository, tag: image.tag, digest: image.digest, pullPolicy: "IfNotPresent" };
        });
        const refs: HelmImageRefFlags[] = refEntries.flatMap(([key, prefix]) => {
          const image = input[key];
          return image ? [{ prefix, repository: image.repository, tag: image.tag, digest: image.digest }] : [];
        });
        const helm = { values: [...target.values, ...generated], set: target.set, images: flags, imageRefs: refs };
        const { release, namespace } = target;

        if (target.kind === "ssh") {
          const key = ctx.secrets[sshKey!.name];
          if (!key) throw new Error(`secret ${sshKey!.name} (sshKey) is not set`);
          await deployOverSsh(ctx, {
            host: target.host, key, chartDir, release, namespace,
            hostImages: flags.map((f) => ({ repository: f.repository, tag: f.tag, digest: f.digest })),
            flags: helm, deployments: deployments ?? [release], rolloutTimeoutSeconds: rolloutTimeout,
          });
        } else {
          if (!existsSync(SERVICE_ACCOUNT_TOKEN))
            throw new Error(`no ServiceAccount token in the step pod: the instance grants no step ServiceAccount (Kubernetes:StepServiceAccount), so an inCluster target can't be deployed`);
          const home = join(ctx.tmpDir, "helm-home");
          ctx.log("helm upgrade", { release, namespace, images: flags.map((f) => `${f.repository}@${f.digest}`).join(" ") });
          await ctx.exec(["helm", "upgrade", "--install", release, ".", "-n", namespace, ...helmFlags(helm), "--wait", `--timeout=${rolloutTimeout}s`], {
            cwd: chartDir,
            env: { HOME: home, HELM_CACHE_HOME: join(home, "cache"), HELM_CONFIG_HOME: join(home, "config"), HELM_DATA_HOME: join(home, "data") },
          });
        }

        if (def.healthPath !== undefined) await httpHealthCheck(ctx, `${serviceUrl({ release, namespace })}${def.healthPath}`, { attempts: 12, delaySeconds: 5 });
        const main = flags[0]!;
        return { release, namespace, image: `${main.repository}:${main.tag}@${main.digest}` };
      },
    },
    configurable,
  ) as never;
}
