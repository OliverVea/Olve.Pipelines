import { config, helmTarget, secret, secretRef, type ConfigRef } from "@olivervea/pipelines";
import { base, checkout, steps, z } from "../fixtures2";
import type { Equal, Expect } from "../type-assert";

const KEY = secret("SSH_PRIVATE_KEY", "ssh key");
const REG = secret("REGISTRY_PASSWORD", "registry password");
const TARGET = config("target", "Where to deploy.", z.object({ beta: helmTarget({ namespace: "olve" }), prod: helmTarget() }));
const PUBLISH = config("publish", "Where images go.", z.object({
  repository: z.string(),
  push: z.boolean().default(true),
  auth: z.object({ username: z.string(), password: secretRef(REG) }).optional(),
  name: z.string(),
}));
const AUTH = config("auth", "Auth.", z.discriminatedUnion("mode", [z.object({ mode: z.literal("none") }), z.object({ mode: z.literal("dex"), issuer: z.string() })]));

// A field reference has the field's type (output view: defaults applied, optional stays optional).
type _ns = Expect<Equal<typeof TARGET.beta.namespace, typeof TARGET.beta.namespace & ConfigRef<string>>>;
const _auth: ConfigRef<{ username: string; password: { secret: "REGISTRY_PASSWORD" } } | undefined> = PUBLISH.auth;
// Union fields common to every variant are reachable; the entry's own `name` is reached through `$`.
const _mode: ConfigRef<"none" | "dex"> = AUTH.mode;
const _name: ConfigRef<string> = PUBLISH.$.name;
void _mode, _name, _auth;

const build = steps.imageBuild({
  name: "build", description: "", repository: PUBLISH.repository, push: true, enabled: PUBLISH.push,
  registryAuth: PUBLISH.auth, secrets: [REG],
});
const deploy = steps.helmDeploy({
  name: "deploy", description: "", target: TARGET.beta, sshKey: KEY, chart: "helm",
  inlineValues: [{ auth: AUTH }], images: { image: "image" },
});
const ssh = steps.helmDeployOverSsh({ name: "ssh", description: "", host: PUBLISH.repository, sshKey: KEY, release: "a", namespace: TARGET.prod.namespace, chart: "helm", values: TARGET.prod.values });
void ssh;

base()
  .stage([checkout, from => ({ repo: from.input.repo, sha: from.input.sha })])
  .stage(build)
  .stage([deploy, from => ({ source: from.checkout, image: from.build })]);
