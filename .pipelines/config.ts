import { config, deployTarget, secret, secretRef, z } from '@olivervea/pipelines';

export const GITHUB_TOKEN = secret(
  'GITHUB_TOKEN',
  'GitHub read token used to fetch the repo tarball.',
);
export const SSH_PRIVATE_KEY = secret(
  'SSH_PRIVATE_KEY',
  'SSH key for ssh deploy targets: pulling the images and running helm on the host.',
);
export const NPM_TOKEN = secret(
  'NPM_TOKEN',
  'npm token that publishes @olivervea/pipelines and moves its dist-tags.',
);
export const GHCR_TOKEN = secret(
  'GHCR_TOKEN',
  'Classic GitHub token: write:packages for images published to ghcr.io, repo for the GitHub releases promote creates.',
);
export const REGISTRY_PASSWORD = secret(
  'REGISTRY_PASSWORD',
  'Password of another image registry, when publish names it.',
);
export const BETA_TOKEN = secret(
  'BETA_TOKEN',
  "Machine token of beta (its Auth:Tokens:deployer): beta-test reconciles and runs beta's test fixture with it.",
);
export const DEPLOYER_TOKEN = secret(
  'DEPLOYER_TOKEN',
  'Bearer token of the Docker Compose deployer, for compose targets.',
);

const Auth = z.discriminatedUnion('mode', [
  z.object({
    mode: z
      .literal('none')
      .describe(
        'Every endpoint is anonymous. Only for local or trusted-network instances.',
      ),
  }),
  z.object({
    mode: z.literal('dex'),
    issuer: z.url(),
    clientId: z.string().default('olve-pipelines'),
    frontendClientId: z.string().optional(),
    allow: z
      .object({
        users: z
          .array(z.string())
          .default([])
          .describe("GitHub logins, matched against Dex's preferred_username claim."),
        orgs: z
          .array(z.string())
          .default([])
          .describe('GitHub orgs or org:team, matched against the groups claim.'),
        emails: z.array(z.string()).default([]),
      })
      .prefault({})
      .describe('Who may use the instance; dex mode needs at least one entry.'),
  }),
  z.object({
    mode: z.literal('authentik'),
    authority: z.url(),
    audience: z.string(),
    frontendAuthority: z.url(),
    frontendAudience: z.string(),
  }),
]);

const Telemetry = z.discriminatedUnion('exporter', [
  z.object({ exporter: z.literal('none') }),
  z.object({
    exporter: z.literal('otlp'),
    endpoint: z.url(),
    protocol: z.enum(['http', 'grpc']).default('http'),
    headers: z
      .object({ kubernetesSecret: z.object({ name: z.string(), key: z.string() }) })
      .optional()
      .describe(
        'A Secret in the target namespace holding OTLP headers ("k1=v1,k2=v2"), e.g. a backend\'s API key.',
      ),
  }),
]);

export const AUTH = config(
  'auth',
  'How users authenticate to the deployed instance.',
  Auth,
);

export const TELEMETRY = config(
  'telemetry',
  'Where the deployed instance exports telemetry.',
  Telemetry.prefault({ exporter: 'none' }),
);

const stageOverrides = {
  auth: Auth.optional().describe('Replaces `auth` for this target.'),
  telemetry: Telemetry.optional().describe('Replaces `telemetry` for this target.'),
};
export const TARGET = config(
  'target',
  'Where beta and prod are deployed, and how: inCluster, ssh or compose; beta may be none.',
  z.object({
    beta: deployTarget({
      namespace: 'olve-pipelines-beta',
      release: 'olve-pipelines',
      extend: stageOverrides,
      composeToken: DEPLOYER_TOKEN,
      optional: true,
    }),
    prod: deployTarget({
      namespace: 'olve-pipelines',
      release: 'olve-pipelines',
      extend: stageOverrides,
      composeToken: DEPLOYER_TOKEN,
    }),
  }),
);

export const SOURCE = config(
  'source',
  'Where the source tarball is fetched from.',
  z
    .object({
      apiUrl: z.string().default('https://api.github.com'),
    })
    .prefault({}),
);

const RegistryAuth = z.object({
  username: z.string(),
  password: z.union([secretRef(GHCR_TOKEN), secretRef(REGISTRY_PASSWORD)]),
});
const Registry = {
  repository: z
    .string()
    .describe('Registry repository, e.g. ghcr.io/you/olve-pipelines.'),
  insecure: z.boolean().default(false).describe('The registry speaks plain HTTP.'),
  auth: RegistryAuth.optional(),
};
const Image = {
  ...Registry,
  release: z
    .array(z.object(Registry))
    .default([])
    .describe(
      'Public repositories the verified image is copied to: its build tag and the prerelease tag when published, the release tag when promoted.',
    ),
};

export const BUILD = config(
  'build',
  'How the server is compiled.',
  z
    .object({
      runtime: z
        .enum(['aot', 'jit'])
        .default('aot')
        .describe(
          'aot: Native AOT on runtime-deps (fastest start, least memory, minutes to compile). jit: framework-dependent on the ASP.NET image (compiles in seconds, starts in about a second).',
        ),
    })
    .prefault({}),
);

export const PUBLISH = config(
  'publish',
  'Where releases go: images, the npm package and the pl CLI.',
  z.object({
    server: z.object({
      ...Image,
      registryMap: z
        .record(z.string(), z.string())
        .optional()
        .describe(
          'Pull-through caches for the base image (registry → mirror prefix); unset = none.',
        ),
    }),
    harness: z.object({
      ...Image,
      registryMap: z
        .record(z.string(), z.string())
        .optional()
        .describe(
          "Pull-through caches for the harness image's base images (registry → mirror prefix); unset = none.",
        ),
      push: z
        .boolean()
        .default(true)
        .describe('false: the deployed server uses its built-in harness image.'),
    }),
    imageTags: z
      .object({
        prerelease: z.string().default('next'),
        release: z.string().default('latest'),
      })
      .prefault({}),
    npm: z
      .object({
        enabled: z.boolean().default(false),
        prereleaseTag: z.string().default('next'),
        releaseTag: z.string().default('latest'),
      })
      .prefault({}),
    cli: z
      .object({
        enabled: z
          .boolean()
          .default(true)
          .describe('Serve the pl built by this run at /download.'),
        github: z
          .object({
            enabled: z
              .boolean()
              .default(false)
              .describe(
                'Promote creates a GitHub release in the source repo, with pl attached (needs GHCR_TOKEN with the repo scope).',
              ),
            prerelease: z
              .boolean()
              .default(false)
              .describe(
                "Mark releases as prereleases; GitHub's latest release then doesn't move.",
              ),
          })
          .prefault({}),
      })
      .prefault({}),
  }),
);
