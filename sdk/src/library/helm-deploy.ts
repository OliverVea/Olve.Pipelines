// steps.helmDeploy — helm-upgrade a release from the source tree's chart onto a target chosen by
// config: the cluster the step runs in (`inCluster`, with the step ServiceAccount's token), or a k3s
// host over SSH (`ssh`). Images come from a registry, pinned by digest.
import { existsSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { z } from "zod";
import type { Ctx } from "../ctx/types.ts";
import type { Configurable } from "../core/config.ts";
import { recordConfigRefs, resolveConfigRefs } from "../core/config-refs.ts";
import type { FileRef } from "../core/models.ts";
import { optional } from "../core/models.ts";
import type { Secret } from "../core/secrets.ts";
import { makeStep, type LibraryStep, type LiteralName, type Step } from "../core/steps.ts";
import type { Simplify } from "../core/types.ts";
import { serviceUrl } from "./checks.ts";
import { chartDirectory, deployOverSsh, DEFAULT_ROLLOUT_TIMEOUT_SECONDS, helmFlags, type HelmImageFlags, type HelmImageRefFlags } from "./helm-deploy-ssh.ts";
import { httpHealthCheck } from "./http-health.ts";
import { HelmRelease, PushedImage, Source } from "./models.ts";

/** helm 3 on Alpine (`apk add openssh-client` for `ssh` targets; the health check runs in the harness). */
export const HELM_IMAGE = "alpine/helm:3.16.2";

/** Where the step pod finds its ServiceAccount token (mounted only with the instance's step ServiceAccount). */
export const SERVICE_ACCOUNT_TOKEN = "/var/run/secrets/kubernetes.io/serviceaccount/token";

/**
 * The config schema of a deploy target, for a pipeline's own config entry:
 * `config("target", "…", z.object({ beta: helmTarget(), prod: helmTarget() }))`.
 * `defaults` fill `namespace` and `release`; `extend` adds fields to both kinds.
 */
export function helmTarget<const E extends z.ZodRawShape = {}>(defaults: { namespace?: string; release?: string } = {}, extend?: E) {
  const name = (d: string | undefined) => (d === undefined ? z.string() : z.string().default(d));
  const common = {
    namespace: name(defaults.namespace).describe("Namespace the release is installed into."),
    release: name(defaults.release).describe("Helm release name."),
    values: z.array(z.string()).default([]).describe("Values files, relative to the chart directory (-f, in order)."),
    set: z.record(z.string(), z.string()).default({}).describe("Extra --set flags."),
    ...(extend ?? ({} as E)),
  };
  return z.discriminatedUnion("kind", [
    z.object({ kind: z.literal("inCluster").describe("helm runs in the step pod against its own cluster (needs the instance's step ServiceAccount)."), ...common }),
    z.object({ kind: z.literal("ssh").describe("helm runs on a k3s host over SSH."), host: z.string().describe("user@host"), ...common }),
  ]);
}

/** A resolved deploy target (the output of `helmTarget()`). */
export type HelmTargetValue =
  | { kind: "inCluster"; namespace: string; release: string; values: readonly string[]; set: Readonly<Record<string, string>> }
  | { kind: "ssh"; host: string; namespace: string; release: string; values: readonly string[]; set: Readonly<Record<string, string>> };

type DefaultImages = { readonly image: "image" };
export type HelmDeployInput<Im extends Record<string, string> = DefaultImages, Rf extends Record<string, string> = {}> = Simplify<
  { readonly source: typeof Source } & { readonly [K in keyof Im]: typeof PushedImage } & { readonly [K in keyof Rf]: ReturnType<typeof optional<typeof PushedImage>> }
>;

export interface HelmDeployDef<N extends string, K extends string, Im extends Record<string, string> = DefaultImages, Rf extends Record<string, string> = {}> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /** Where to deploy: usually a config reference (`TARGET.beta`) to a `helmTarget()` value. */
  target: Configurable<HelmTargetValue>;
  /** Private key for `ssh` targets. */
  sshKey?: Secret<K>;
  /** Chart directory inside the source tree, e.g. `helm`. */
  chart: string;
  /**
   * Values written as files and passed after the target's values files (`-f`, in order). Config
   * references are resolved first; undefined members are dropped, so `{ auth: TARGET.beta.auth }`
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
  /** `ssh` targets: rollouts to wait for (`deploy/<name>` for a bare name). Default: the release name. */
  deployments?: readonly string[];
  /** Rollout wait (`ssh`: per rollout; `inCluster`: `helm --wait --timeout`). Default 120. */
  rolloutTimeoutSeconds?: number;
  /** After the rollout, GET `http://<release>.<namespace>.svc.cluster.local<healthPath>` until it returns 2xx. */
  healthPath?: string;
  timeoutSeconds?: number;
}

/**
 * Deploys registry images with the Helm chart at `<source>/<chart>` to the target's namespace:
 * `inCluster` runs `helm upgrade --install --wait` in the step pod with the step ServiceAccount's
 * token; `ssh` pulls the images on the host and runs helm there. Then (with `healthPath`) polls the
 * release's Service.
 */
export function helmDeploy<
  const N extends string,
  const K extends string = never,
  const Im extends Record<string, string> = DefaultImages,
  const Rf extends Record<string, string> = {},
>(def: HelmDeployDef<N, K, Im, Rf>): Step<N, HelmDeployInput<Im, Rf>, typeof HelmRelease, K> & LibraryStep {
  const images: Record<string, string> = def.images ?? { image: "image" };
  const imageEntries = Object.entries(images);
  const imageRefs: Record<string, string> = def.imageRefs ?? {};
  const refEntries = Object.entries(imageRefs);
  const rolloutTimeout = def.rolloutTimeoutSeconds ?? DEFAULT_ROLLOUT_TIMEOUT_SECONDS;
  if (imageEntries.length === 0) throw new Error(`helmDeploy '${def.name}': images must not be empty`);
  if ("source" in images || "source" in imageRefs) throw new Error(`helmDeploy '${def.name}': 'source' can't be an image input`);
  for (const [key] of refEntries) if (key in images) throw new Error(`helmDeploy '${def.name}': '${key}' is both in images and imageRefs`);
  if (def.deployments?.length === 0) throw new Error(`helmDeploy '${def.name}': deployments must not be empty`);
  if (def.healthPath !== undefined && !def.healthPath.startsWith("/")) throw new Error(`helmDeploy '${def.name}': healthPath must start with '/'`);
  const input = {
    source: Source,
    ...Object.fromEntries(imageEntries.map(([key]) => [key, PushedImage])),
    ...Object.fromEntries(refEntries.map(([key]) => [key, optional(PushedImage)])),
  };
  const configurable = { target: def.target, inlineValues: def.inlineValues ?? [] };

  return makeStep(
    "helmDeploy",
    {
      name: def.name, description: def.description, image: HELM_IMAGE, input, output: HelmRelease,
      secrets: def.sshKey ? [def.sshKey] : [], timeoutSeconds: def.timeoutSeconds, clusterAccess: true,
    },
    {
      manifestConfig: () => ({
        target: recordConfigRefs(def.target),
        ...(def.sshKey ? { sshKey: def.sshKey.name } : {}),
        chart: def.chart,
        ...(def.inlineValues?.length ? { inlineValues: recordConfigRefs(def.inlineValues) } : {}),
        images: { ...images },
        ...(refEntries.length > 0 ? { imageRefs: { ...imageRefs } } : {}),
        ...(def.deployments ? { deployments: [...def.deployments] } : {}),
        ...(def.rolloutTimeoutSeconds !== undefined ? { rolloutTimeoutSeconds: def.rolloutTimeoutSeconds } : {}),
        ...(def.healthPath !== undefined ? { healthPath: def.healthPath } : {}),
      }),
      async run(input: Record<string, { repository: string; tag: string; digest: string } | undefined> & { source: { tree: FileRef } }, ctx: Ctx) {
        const { target, inlineValues } = resolveConfigRefs(configurable, ctx.config ?? {}) as { target: HelmTargetValue; inlineValues: Record<string, unknown>[] };
        if (target?.kind !== "inCluster" && target?.kind !== "ssh") throw new Error(`helmDeploy '${def.name}': target must be a helmTarget() value (kind inCluster or ssh)`);
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
          if (!def.sshKey) throw new Error(`helmDeploy '${def.name}': an ssh target needs sshKey`);
          const key = ctx.secrets[def.sshKey.name];
          if (!key) throw new Error(`secret ${def.sshKey.name} (sshKey) is not set`);
          await deployOverSsh(ctx, {
            host: target.host, key, chartDir, release, namespace,
            hostImages: flags.map((f) => ({ repository: f.repository, tag: f.tag, digest: f.digest })),
            flags: helm, deployments: def.deployments ?? [release], rolloutTimeoutSeconds: rolloutTimeout,
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
