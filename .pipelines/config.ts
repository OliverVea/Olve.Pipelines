import { config, helmTarget, secret, secretRef, z } from "@olivervea/pipelines";

export const GITHUB_TOKEN = secret("GITHUB_TOKEN", "GitHub read token used to fetch the repo tarball.");
export const SSH_PRIVATE_KEY = secret("SSH_PRIVATE_KEY", "SSH key for ssh deploy targets: pulling the images and running helm on the host.");
export const NPM_TOKEN = secret("NPM_TOKEN", "npm token that publishes @olivervea/pipelines and moves its dist-tags.");
export const GHCR_TOKEN = secret("GHCR_TOKEN", "GitHub token with write:packages, for images published to ghcr.io.");
export const REGISTRY_PASSWORD = secret("REGISTRY_PASSWORD", "Password of another image registry, when publish names it.");

const Auth = z.discriminatedUnion("mode", [
  z.object({ mode: z.literal("none").describe("Every endpoint is anonymous. Only for local or trusted-network instances.") }),
  z.object({
    mode: z.literal("dex"),
    issuer: z.url(),
    clientId: z.string().default("olve-pipelines"),
    frontendClientId: z.string().optional(),
    anonymousReads: z.boolean().default(false),
    allow: z.object({
      users: z.array(z.string()).default([]).describe("GitHub logins, matched against Dex's preferred_username claim."),
      orgs: z.array(z.string()).default([]).describe("GitHub orgs or org:team, matched against the groups claim."),
      emails: z.array(z.string()).default([]),
    }).prefault({}).describe("Who may sign in. Not enforced by the server yet."),
  }),
  z.object({
    mode: z.literal("authentik"),
    authority: z.url(),
    audience: z.string(),
    frontendAuthority: z.url(),
    frontendAudience: z.string(),
    anonymousReads: z.boolean().default(false),
  }),
]);

const Telemetry = z.discriminatedUnion("exporter", [
  z.object({ exporter: z.literal("none") }),
  z.object({
    exporter: z.literal("otlp"),
    endpoint: z.url(),
    protocol: z.enum(["http", "grpc"]).default("http"),
    auth: z.discriminatedUnion("type", [
      z.object({ type: z.literal("none") }),
      z.object({
        type: z.literal("oauth2"),
        tokenUrl: z.url(),
        clientId: z.string(),
        scope: z.string().optional(),
        clientSecret: z.object({ kubernetesSecret: z.object({ name: z.string(), key: z.string() }) })
          .describe("A Secret in the target namespace that holds the client secret."),
      }),
    ]).prefault({ type: "none" }),
  }),
]);

export const AUTH = config("auth", "How users authenticate to the deployed instance.", Auth);

export const TELEMETRY = config("telemetry", "Where the deployed instance exports telemetry.", Telemetry.prefault({ exporter: "none" }));

const stageOverrides = {
  auth: Auth.optional().describe("Replaces `auth` for this target."),
  telemetry: Telemetry.optional().describe("Replaces `telemetry` for this target."),
};
export const TARGET = config("target", "Where beta and prod are deployed, and how (inCluster or ssh).", z.object({
  beta: helmTarget({ namespace: "olve-pipelines-beta", release: "olve-pipelines" }, stageOverrides),
  prod: helmTarget({ namespace: "olve-pipelines", release: "olve-pipelines" }, stageOverrides),
}));

export const SOURCE = config("source", "Where the source tarball is fetched from.", z.object({
  apiUrl: z.string().default("https://api.github.com"),
}).prefault({}));

const RegistryAuth = z.object({ username: z.string(), password: z.union([secretRef(GHCR_TOKEN), secretRef(REGISTRY_PASSWORD)]) });
const Image = {
  repository: z.string().describe("Registry repository, e.g. ghcr.io/you/olve-pipelines."),
  insecure: z.boolean().default(false).describe("The registry speaks plain HTTP."),
  auth: RegistryAuth.optional(),
};

export const PUBLISH = config("publish", "Where releases go: images, the npm package and the pl CLI.", z.object({
  server: z.object({
    ...Image,
    cacheRepo: z.string().optional().describe("kaniko layer cache repository."),
    registryMap: z.record(z.string(), z.string()).optional().describe("kaniko --registry-map (pull-through caches); unset = the SDK default."),
  }),
  harness: z.object({
    ...Image,
    push: z.boolean().default(true).describe("false: the deployed server uses its built-in harness image."),
  }),
  imageTags: z.object({ prerelease: z.string().default("next"), release: z.string().default("latest") }).prefault({}),
  npm: z.object({
    enabled: z.boolean().default(false),
    prereleaseTag: z.string().default("next"),
    releaseTag: z.string().default("latest"),
  }).prefault({}),
  cli: z.object({ enabled: z.boolean().default(true).describe("Serve the pl built by this run at /download.") }).prefault({}),
}));
