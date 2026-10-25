// steps.composeDeploy — ask a Docker Compose deployer to recreate a service with an image. The step is
// thin on purpose: the deployer (pull, recreate, health, rollback) lives beside the compose project
// and is fixed there, behind a versioned HTTP API, without editing pipelines.
import type { Ctx } from "../ctx/types.ts";
import type { Configurable } from "../core/config.ts";
import { resolveConfigRefs } from "../core/config-refs.ts";
import { implement, type Implementation } from "../core/handlers.ts";
import type { Secret } from "../core/secrets.ts";
import { step, type LiteralName, type Step } from "../core/steps.ts";
import { httpHealthCheck } from "./http-health.ts";
import { HelmRelease, PushedImage } from "./models.ts";

/** The step's own image: the work is one HTTP call from the harness. */
export const COMPOSE_DEPLOY_IMAGE = "alpine:3.22";

/** A resolved `kind: compose` target. */
export interface ComposeTargetValue {
  kind: "compose";
  deployerUrl: string;
  service: string;
  token: { secret: string };
  healthUrl?: string | undefined;
}

/** What the deployer answers to `POST /v1/deploy`. */
export interface ComposeDeployResult {
  status: "deployed" | "rolled-back" | "failed";
  service: string;
  image: string;
  previousImage?: string;
  message?: string;
  log?: string[];
}

export interface ComposeDeployDef<N extends string, K extends string> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /** A compose target, usually a `select` case's reference. */
  target: Configurable<ComposeTargetValue>;
  /** The secret the target's `token` names (declared by the step). */
  token: Secret<K>;
  timeoutSeconds?: number;
}

const input = { image: PushedImage };

/**
 * Deploys a registry image (by digest) to a compose service: `POST <deployerUrl>/v1/deploy
 * { service, image: <repository>@<digest> }` with the target's bearer token. The deployer pulls the
 * image, recreates the service, waits for its health and rolls back on failure; the step reports its
 * answer, then (with the target's `healthUrl`) checks health itself. Output: a `HelmRelease` with the
 * service as `release` and `compose` as `namespace`, so it can share a step name with the helm deploys.
 */
export function composeDeploy<const N extends string, const K extends string>(
  def: ComposeDeployDef<N, K>,
): Implementation<Step<N, typeof input, typeof HelmRelease, K>> {
  const contract = step({
    name: def.name,
    description: def.description,
    kind: "composeDeploy",
    image: COMPOSE_DEPLOY_IMAGE,
    input,
    output: HelmRelease,
    secrets: [def.token],
    timeoutSeconds: def.timeoutSeconds,
    settings: { target: def.target, token: def.token.name },
  });
  return implement(contract, async ({ image }, ctx: Ctx) => {
    const target = resolveConfigRefs(def.target, ctx.config ?? {}) as ComposeTargetValue;
    if (target?.kind !== "compose") throw new Error(`composeDeploy '${def.name}': the target is kind '${String(target?.kind)}', not 'compose'`);
    if (target.token.secret !== def.token.name) throw new Error(`composeDeploy '${def.name}': the target's token names secret ${target.token.secret}, but the step declares ${def.token.name}`);
    const token = (ctx.secrets as Record<string, string>)[def.token.name];
    const ref = `${image.repository}@${image.digest}`;
    const url = `${target.deployerUrl.replace(/\/+$/, "")}/v1/deploy`;
    ctx.log("deploying", { service: target.service, image: ref, deployer: target.deployerUrl });
    const res = await ctx.http.fetch(url, {
      method: "POST",
      headers: { authorization: `Bearer ${token}`, "content-type": "application/json" },
      body: JSON.stringify({ service: target.service, image: ref }),
    });
    const text = await res.text();
    let result: ComposeDeployResult | undefined;
    try {
      result = JSON.parse(text) as ComposeDeployResult;
    } catch {
      // reported below
    }
    for (const line of result?.log ?? []) ctx.log(`deployer: ${line}`);
    if (!res.ok || result?.status !== "deployed")
      throw new Error(`the deployer answered HTTP ${res.status}: ${result ? `${result.status}${result.message ? `: ${result.message}` : ""}` : text.slice(0, 300)}`);
    if (target.healthUrl) await httpHealthCheck(ctx, target.healthUrl, { attempts: 12, delaySeconds: 5 });
    ctx.log("deployed", { service: target.service, image: ref, previous: result.previousImage ?? "-" });
    return { release: target.service, namespace: "compose", image: ref };
  }) as never;
}
