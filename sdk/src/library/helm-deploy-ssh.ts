// steps.helmDeployOverSsh — import a built image into a k3s host's containerd and helm-upgrade a
// release there, over SSH; optionally verify an HTTP health endpoint afterwards.
//
// Ported from v1's olve-lib.sh (olve_ssh_host, olve_image_import, olve_helm_deploy) and
// deploy-beta.sh (rollout wait + health loop); the footguns are kept as comments below.
import { chmodSync, existsSync, mkdirSync, statSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { Ctx } from "../ctx/types.ts";
import type { FileRef } from "../core/models.ts";
import type { Secret } from "../core/secrets.ts";
import { makeStep, type LibraryStep, type LiteralName, type Step } from "../core/steps.ts";
import { BuiltImage, HelmRelease, Source } from "./models.ts";

/** Alpine: `apk add openssh-client` at run time gives ssh/scp (the health check runs in the harness). */
export const HELM_DEPLOY_IMAGE = "alpine:3.22";

/**
 * Footgun: import via the k3s containerd socket, NOT the default one — kubelet only sees images in
 * the k3s instance's `k8s.io` namespace (otherwise pods fail with ErrImageNeverPull).
 */
export const K3S_IMAGE_IMPORT = "sudo nerdctl --address /run/k3s/containerd/containerd.sock --namespace k8s.io load";

/** Health check: attempts × delay (v1: 5 × 5 s). */
const HEALTH_ATTEMPTS = 5;
const HEALTH_DELAY_SECONDS = 5;
const ROLLOUT_TIMEOUT = "120s";

const input = { source: Source, image: BuiltImage };
export type HelmDeployOverSshInput = typeof input;

export interface HelmDeployOverSshDef<N extends string, K extends string> {
  name: LiteralName<N>;
  /** SSH target, `user@host`. The user needs passwordless sudo for nerdctl, and helm/kubectl with a kubeconfig. */
  host: string;
  /** Private key for `host`. */
  sshKey: Secret<K>;
  /** Helm release name; the chart's Deployment is expected to be named the same (rollout wait). */
  release: string;
  namespace: string;
  /** Chart directory inside the source tree, e.g. `helm`. */
  chart: string;
  /** Values files, relative to the chart directory (`-f`, in order). */
  values?: readonly string[];
  /**
   * Chart-specific `--set` flags (e.g. `{ "slo.enabled": "false" }`). The image flags are added by
   * the step and always win: `image.repository`, `image.tag`, `image.pullPolicy=Never`.
   */
  set?: Readonly<Record<string, string>>;
  /** After the rollout, GET this URL until it returns 2xx (or fail the step). */
  healthUrl?: string;
  timeoutSeconds?: number;
}

const SAFE = /^[A-Za-z0-9_\/.:=@%+,-]+$/;
/** POSIX shell quoting (safe words are left bare, so logs stay readable). */
export function shq(s: string): string {
  return SAFE.test(s) ? s : `'${s.replace(/'/g, `'\\''`)}'`;
}

/** Remote `helm upgrade --install`, run from inside the copied chart directory. */
export function helmUpgradeCommand(o: {
  remoteChart: string;
  release: string;
  namespace: string;
  values: readonly string[];
  set: Readonly<Record<string, string>>;
  repository: string;
  tag: string;
}): string {
  // Chart-relative values paths (`-f values-beta.yaml`) resolve because helm runs from inside the
  // copied chart dir. Extra flags come before the image flags, so the image overrides win. The image
  // is local to the node (imported above), never pulled: pullPolicy=Never.
  const flags = [
    ...o.values.flatMap((v) => ["-f", v]),
    ...Object.entries(o.set).flatMap(([k, v]) => ["--set", `${k}=${v}`]),
    "--set", `image.repository=${o.repository}`,
    "--set", `image.tag=${o.tag}`,
    "--set", "image.pullPolicy=Never",
  ];
  return [
    `cd ${shq(o.remoteChart)}`,
    ["helm", "upgrade", "--install", o.release, ".", "-n", o.namespace, ...flags].map(shq).join(" "),
    `cd / && rm -rf ${shq(o.remoteChart)}`,
  ].join(" && ");
}

/**
 * Deploys `image` with the Helm chart at `<source>/<chart>` on a k3s host over SSH:
 * stream the image tar into the host's k3s containerd, copy the chart, `helm upgrade --install`,
 * wait for `deploy/<release>` to roll out, then (with `healthUrl`) poll the health endpoint.
 */
export function helmDeployOverSsh<const N extends string, const K extends string>(
  def: HelmDeployOverSshDef<N, K>,
): Step<N, HelmDeployOverSshInput, typeof HelmRelease, K> & LibraryStep {
  const values = def.values ?? [];
  const set = def.set ?? {};
  return makeStep(
    "helmDeployOverSsh",
    { name: def.name, image: HELM_DEPLOY_IMAGE, input, output: HelmRelease, secrets: [def.sshKey], timeoutSeconds: def.timeoutSeconds },
    {
      manifestConfig: () => ({
        host: def.host,
        sshKey: def.sshKey.name,
        release: def.release,
        namespace: def.namespace,
        chart: def.chart,
        values: [...values],
        set: { ...set },
        ...(def.healthUrl ? { healthUrl: def.healthUrl } : {}),
      }),
      async run(
        { source, image }: { source: { tree: FileRef }; image: { repository: string; tag: string; tar: FileRef } },
        ctx: Ctx,
      ) {
        const tree = await ctx.files.fetch(source.tree);
        const chartDir = join(tree, def.chart);
        if (!existsSync(chartDir) || !statSync(chartDir).isDirectory()) throw new Error(`chart directory '${def.chart}' not found in the source tree`);
        const imageTar = await ctx.files.fetch(image.tar);

        // SSH setup. The key file needs a trailing newline (OpenSSH rejects the key otherwise) and
        // mode 600. The host key is not verified (as in v1): the host is reached over the tailnet.
        await ctx.exec(["apk", "add", "--no-cache", "openssh-client"]);
        const sshDir = join(ctx.tmpDir, "ssh");
        mkdirSync(sshDir, { recursive: true, mode: 0o700 });
        const keyFile = join(sshDir, "id");
        const key = ctx.secrets[def.sshKey.name]!;
        writeFileSync(keyFile, key.endsWith("\n") ? key : key + "\n", { mode: 0o600 });
        chmodSync(keyFile, 0o600);
        const opts = ["-i", keyFile, "-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=/dev/null", "-o", "LogLevel=ERROR", "-o", "BatchMode=yes"];
        const ssh = (remote: string) => ctx.exec(["ssh", ...opts, def.host, remote]);

        const ref = `${image.repository}:${image.tag}`;
        ctx.log("importing image", { image: ref, host: def.host });
        // Streamed over ssh's stdin (ctx.exec has none, hence the shell redirect).
        await ctx.sh(["ssh", ...opts, def.host, K3S_IMAGE_IMPORT].map(shq).join(" ") + ` < ${shq(imageTar)}`);

        const remoteChart = `/tmp/${def.release}-helm-${def.namespace}`;
        ctx.log("helm upgrade", { release: def.release, namespace: def.namespace, image: ref });
        await ssh(`rm -rf ${shq(remoteChart)}`);
        await ctx.exec(["scp", ...opts, "-r", chartDir, `${def.host}:${remoteChart}`]);
        await ssh(helmUpgradeCommand({ remoteChart, release: def.release, namespace: def.namespace, values, set, repository: image.repository, tag: image.tag }));

        ctx.log("waiting for rollout", { deployment: def.release });
        await ssh(`kubectl -n ${shq(def.namespace)} rollout status ${shq(`deploy/${def.release}`)} --timeout=${ROLLOUT_TIMEOUT}`);

        if (def.healthUrl) {
          const url = def.healthUrl;
          await ctx.retry({ attempts: HEALTH_ATTEMPTS, delaySeconds: HEALTH_DELAY_SECONDS }, () => ctx.http.expectOk(url));
          ctx.log("health OK", { url });
        }
        return { release: def.release, namespace: def.namespace, image: ref };
      },
    },
  ) as never;
}
