// steps.imageBuild — build a container image from a `Source` with kaniko: to an image tarball that
// travels as a file ref (a deploy imports it on the host), or pushed to a registry (`push: true`).
// Ported from v1's olve-lib.sh (olve_kaniko_build + build.sh's layer cache); footguns kept below.
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { join } from 'node:path';
import type { Ctx, RunInfo } from '../ctx/types.ts';
import type { FileRef } from '../core/models.ts';
import type { Secret } from '../core/secrets.ts';
import { isConfigRef, type ConfigRef, type Configurable } from '../core/config.ts';
import { isLiteral, resolveConfigRefs } from '../core/config-refs.ts';
import { implement, type Implementation } from '../core/handlers.ts';
import { step, type LiteralName, type Step } from '../core/steps.ts';
import { sha256File } from '../util/hash.ts';
import { dirEntries, writeTar } from '../util/tar.ts';
import { BuiltImage, PushedImage, Source } from './models.ts';

/** kaniko's debug image (it has a busybox shell for debugging; the executor itself needs none). */
export const KANIKO_IMAGE = 'gcr.io/kaniko-project/executor:debug';

/**
 * Footgun: the build context must live under /kaniko, NOT /workspace (or another plain directory in
 * the container): kaniko wipes the filesystem root between the stages of a multi-stage build, which
 * breaks a later stage's COPY from the context. /kaniko survives stage transitions.
 */
export const KANIKO_CONTEXT = '/kaniko/build-context';

const input = { source: Source };
export type ImageBuildInput = typeof input;

/** Credentials for the registry of `repository`: a user name and a secret holding the password or token. */
export interface RegistryAuth<K extends string> {
  username: string;
  password: Secret<K>;
}

/** Registry credentials as config: the password is a `secretRef` (`{ secret: NAME }`) the step must declare in `secrets`. */
export interface RegistryAuthConfig {
  username: string;
  password: { secret: string };
}

export interface ImageBuildDef<N extends string, K extends string = never> {
  name: LiteralName<N>;
  /** What the step does, shown in the UI. Required; may be empty. */
  description: string;
  /**
   * Image name, e.g. `olve-pipelines-v2`; with `push`, the registry repository to push to
   * (`registry.ovea.pro/app/api`). The tag is the run's version (see `imageTag`) unless `contentTag`.
   */
  repository: Configurable<string>;
  /** Build context: a directory of the source tree with a `Dockerfile` (default: the root). */
  context?: string;
  /**
   * kaniko `--registry-map` (registry → mirror prefix); kaniko falls back to the original registry if
   * the mirror lacks the image. Defaults to the homelab pull-through caches (`DEFAULT_REGISTRY_MAP`);
   * a map replaces the default entirely, `{}` turns mapping off.
   */
  registryMap?: Configurable<Record<string, string> | undefined>;
  timeoutSeconds?: number;
  /**
   * Kill kaniko when it writes no output for this long (default 300 s; 0 disables), failing the
   * attempt so `retries` can start a fresh one. Catches base-image pulls that stall on an idle
   * connection (seen with mcr.microsoft.com), which kaniko itself never times out. Keep it well above
   * the longest silent build phase (an AOT "Generating native code" runs ~2 min with no output).
   */
  stallTimeoutSeconds?: number;
  /**
   * Automatic retries of a failed attempt (a fresh pod: kaniko unpacks the base image over the
   * container root and does not clean up when killed, so retrying inside the same pod is unsafe).
   * A `--no-push` build is idempotent, so the default is 2 (three tries).
   */
  retries?: number;
  /**
   * Log in to the registry of `repository` (e.g. `ghcr.io` with a token that may write packages).
   * Used for kaniko's push, base image and cache pulls from that registry, and the `contentTag` lookup.
   */
  registryAuth?: RegistryAuth<K> | ConfigRef<RegistryAuthConfig | undefined>;
  /** The registry of `repository` speaks plain HTTP (a local `registry:2`). */
  insecureRegistry?: Configurable<boolean | undefined>;
  /** Secrets a config-referenced `registryAuth` may name (the step declares them). */
  secrets?: readonly Secret<K>[];
  /** `false` skips the step (`ctx.skip`), e.g. `enabled: PUBLISH.harness.push`. Default true. */
  enabled?: Configurable<boolean | undefined>;
}

/** Tarball mode (default): output `BuiltImage` with the image tar as a file. */
export interface ImageBuildTarballDef<
  N extends string,
  K extends string = never,
> extends ImageBuildDef<N, K> {
  push?: false;
  contentTag?: never;
}

/**
 * Registry mode: push to `repository` and output `PushedImage`, for images too big to travel as a
 * tarball (the step's scratch space is ~1 GiB).
 */
export interface ImageBuildPushDef<
  N extends string,
  K extends string = never,
> extends ImageBuildDef<N, K> {
  push: true;
  /**
   * Tag the image `src-<hash>`, the hash of the build context's content, and skip the build when the
   * registry already has that tag: the image is rebuilt only when its context changes.
   */
  contentTag?: boolean;
}

/** Default `registryMap`: none, kaniko pulls from the registries themselves. */
export const DEFAULT_REGISTRY_MAP: Readonly<Record<string, string>> = {};

/** How long a cache registry gets to answer `GET /v2/` before the build goes without it. */
export const CACHE_PROBE_TIMEOUT_MS = 5000;

/** The registry host of an image reference or mirror prefix (`registry.ovea.pro/a/b` → `registry.ovea.pro`). */
export function registryHost(ref: string): string {
  return ref.split('/')[0]!;
}

/** Whether a registry answers its API root: 200, or 401 (it wants a token). Never throws. */
export async function registryUp(
  http: Ctx['http'],
  host: string,
  signal?: AbortSignal,
  timeoutMs = CACHE_PROBE_TIMEOUT_MS,
  insecure = false,
): Promise<boolean> {
  try {
    const timeout = AbortSignal.timeout(timeoutMs);
    const res = await http.fetch(`${insecure ? 'http' : 'https'}://${host}/v2/`, {
      signal: signal ? AbortSignal.any([signal, timeout]) : timeout,
    });
    await res.body?.cancel().catch(() => {});
    return res.status === 200 || res.status === 401;
  } catch {
    return false;
  }
}

/** The registry map without the mirrors that don't answer, so a mirror outage (GC, maintenance) never blocks a build. */
export async function reachableMirrors(
  ctx: Pick<Ctx, 'http' | 'log' | 'signal'>,
  registryMap: Record<string, string>,
  insecureHosts: readonly string[] = [],
): Promise<Record<string, string>> {
  const hosts = new Set(Object.values(registryMap).map(registryHost));
  const up = new Map(
    await Promise.all(
      [...hosts].map(
        async (h) =>
          [
            h,
            await registryUp(
              ctx.http,
              h,
              ctx.signal,
              undefined,
              insecureHosts.includes(h),
            ),
          ] as const,
      ),
    ),
  );
  const reachable: Record<string, string> = {};
  for (const [from, to] of Object.entries(registryMap)) {
    if (up.get(registryHost(to))) reachable[from] = to;
    else ctx.log(`${registryHost(to)} unreachable; pulling from ${from} directly`);
  }
  return reachable;
}

/** Default `retries`: a stalled or flaky build gets two fresh attempts before the run fails. */
export const DEFAULT_RETRIES = 2;

/** Default `stallTimeoutSeconds`: well above the longest silent phase of a normal build. */
export const DEFAULT_STALL_TIMEOUT_SECONDS = 300;

/**
 * The image tag for this run: the run's version stamp, plus `-attempt<n>` on reruns. A rebuilt image
 * must not reuse the tag: deploys import it with `pullPolicy=Never`, so an unchanged tag would leave
 * the old pods running.
 */
export function imageTag(run: Pick<RunInfo, 'version' | 'attempt'>): string {
  const tag = run.attempt > 1 ? `${run.version}-attempt${run.attempt}` : run.version;
  if (!/^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$/.test(tag))
    throw new Error(`'${tag}' (from the run's version stamp) is not a valid image tag`);
  return tag;
}

/** The name a container runtime gives an image after loading it (`x` → `docker.io/library/x`). */
export function qualifyRepository(repository: string): string {
  const first = repository.split('/')[0]!;
  if (!repository.includes('/')) return `docker.io/library/${repository}`;
  if (first.includes('.') || first.includes(':') || first === 'localhost')
    return repository;
  return `docker.io/${repository}`;
}

/** A relative path inside the source tree (no `..`, not absolute). */
function checkRelative(what: string, path: string): string {
  const clean = path.replace(/^\.\/+/, '').replace(/\/+$/, '');
  if (clean.startsWith('/') || clean.split('/').includes('..'))
    throw new Error(
      `imageBuild ${what} '${path}' must be a relative path inside the source tree`,
    );
  return clean === '.' ? '' : clean;
}

/** The `src-<12 hex>` tag of a directory: the sha256 of its deterministic tar (as `ctx.output.dir` writes it). */
export function contentTagOf(dir: string, tmpDir: string): string {
  mkdirSync(tmpDir, { recursive: true });
  const work = mkdtempSync(join(tmpDir, 'content-tag-'));
  try {
    const tar = join(work, 'context.tar');
    writeTar(tar, dirEntries(dir));
    return `src-${sha256File(tar).slice(0, 12)}`;
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
}

const MANIFEST_TYPES = [
  'application/vnd.oci.image.index.v1+json',
  'application/vnd.oci.image.manifest.v1+json',
  'application/vnd.docker.distribution.manifest.list.v2+json',
  'application/vnd.docker.distribution.manifest.v2+json',
].join(', ');

/** Options of a registry API call: login (`username`/`password`) and plain HTTP. */
export interface RegistryAccess {
  username?: string;
  password?: string;
  insecure?: boolean;
}

const basic = (username: string, password: string) =>
  `Basic ${Buffer.from(`${username}:${password}`).toString('base64')}`;

/** `Bearer realm="…",service="…",scope="…"` → its parameters. */
export function parseChallenge(header: string): {
  scheme: string;
  params: Record<string, string>;
} {
  const scheme = header.trim().split(/\s+/, 1)[0]!.toLowerCase();
  const params: Record<string, string> = {};
  for (const m of header.slice(scheme.length).matchAll(/([A-Za-z]+)="([^"]*)"/g))
    params[m[1]!.toLowerCase()] = m[2]!;
  return { scheme, params };
}

/**
 * `HEAD`/`GET` against a registry API path, answering its auth challenge: Basic (a `registry:2` with
 * htpasswd) or a Bearer token from the realm (ghcr.io, Docker Hub; anonymous for public images).
 */
async function registryFetch(
  http: Ctx['http'],
  host: string,
  path: string,
  init: RequestInit,
  access: RegistryAccess,
): Promise<Response> {
  const url = `${access.insecure ? 'http' : 'https'}://${host}${path}`;
  const res = await http.fetch(url, init);
  if (res.status !== 401) return res;
  await res.body?.cancel().catch(() => {});
  const { scheme, params } = parseChallenge(res.headers.get('www-authenticate') ?? '');
  const login =
    access.username !== undefined && access.password !== undefined
      ? basic(access.username, access.password)
      : undefined;
  let authorization: string;
  if (scheme === 'basic') {
    if (!login) return new Response(null, { status: 401 });
    authorization = login;
  } else if (scheme === 'bearer' && params.realm) {
    const q = new URLSearchParams();
    if (params.service) q.set('service', params.service);
    if (params.scope) q.set('scope', params.scope);
    const tokenRes = await http.fetch(`${params.realm}?${q}`, {
      headers: login ? { Authorization: login } : {},
    });
    if (!tokenRes.ok) {
      await tokenRes.body?.cancel().catch(() => {});
      return new Response(null, {
        status: tokenRes.status === 404 ? 401 : tokenRes.status,
      });
    }
    const body = (await tokenRes.json()) as { token?: string; access_token?: string };
    const token = body.token ?? body.access_token;
    if (!token)
      throw new Error(`registry token endpoint ${params.realm} returned no token`);
    authorization = `Bearer ${token}`;
  } else {
    return new Response(null, { status: 401 });
  }
  const headers = new Headers(init.headers);
  headers.set('Authorization', authorization);
  return http.fetch(url, { ...init, headers });
}

/** The digest of `repository:tag` in its registry (`HEAD …/manifests/<tag>`), or undefined if it has none. */
export async function registryDigest(
  http: Ctx['http'],
  repository: string,
  tag: string,
  access: RegistryAccess = {},
): Promise<string | undefined> {
  const [host, ...path] = repository.split('/');
  const res = await registryFetch(
    http,
    host!,
    `/v2/${path.join('/')}/manifests/${tag}`,
    { method: 'HEAD', headers: { Accept: MANIFEST_TYPES } },
    access,
  );
  await res.body?.cancel().catch(() => {});
  if (res.status === 404) return undefined;
  // An anonymous lookup of a repository that doesn't exist yet (or is private) is denied, not 404.
  if ((res.status === 401 || res.status === 403) && access.password === undefined)
    return undefined;
  if (!res.ok)
    throw new Error(
      `registry lookup of ${repository}:${tag} answered HTTP ${res.status}`,
    );
  const digest = res.headers.get('docker-content-digest') ?? '';
  return /^sha256:[0-9a-f]{64}$/.test(digest) ? digest : undefined;
}

/** kaniko reads registry logins from `$DOCKER_CONFIG/config.json` (`/kaniko/.docker` in its image). */
export const KANIKO_DOCKER_CONFIG = '/kaniko/.docker';

/** A docker `config.json` that logs in to `host`. */
export function dockerConfigJson(
  host: string,
  username: string,
  password: string,
): string {
  return JSON.stringify({
    auths: {
      [host]: { auth: Buffer.from(`${username}:${password}`).toString('base64') },
    },
  });
}

/** Adds the login for `host` to `<dir>/config.json` (owner-only), keeping what is there (kaniko's credHelpers). */
export function writeDockerConfig(
  dir: string,
  host: string,
  username: string,
  password: string,
): void {
  mkdirSync(dir, { recursive: true });
  const file = join(dir, 'config.json');
  let config: { auths?: Record<string, unknown> } = {};
  try {
    config = JSON.parse(readFileSync(file, 'utf8'));
  } catch {
    // absent or unreadable: start fresh
  }
  const login = JSON.parse(dockerConfigJson(host, username, password)) as {
    auths: Record<string, unknown>;
  };
  writeFileSync(
    file,
    JSON.stringify({ ...config, auths: { ...config.auths, ...login.auths } }),
    { mode: 0o600 },
  );
}

/**
 * Builds an image with kaniko from `<context>/Dockerfile` in the source tree (default: the root). Default: into an image tarball, output `BuiltImage { repository, tag, digest, tar }`.
 * With `push: true`: pushed to `repository`, output `PushedImage { repository, tag, digest }`.
 */
export function imageBuild<const N extends string, const K extends string = never>(
  def: ImageBuildTarballDef<N, K>,
): Implementation<Step<N, ImageBuildInput, typeof BuiltImage, K>>;
export function imageBuild<const N extends string, const K extends string = never>(
  def: ImageBuildPushDef<N, K>,
): Implementation<Step<N, ImageBuildInput, typeof PushedImage, K>>;
export function imageBuild<const N extends string, const K extends string = never>(
  def: ImageBuildTarballDef<N, K> | ImageBuildPushDef<N, K>,
): AnyImageBuild {
  const push = def.push === true;
  const contentTag = push && def.contentTag === true;
  const context = checkRelative('context', def.context ?? '');
  const authSecret =
    def.registryAuth && !isConfigRef(def.registryAuth) ? def.registryAuth : undefined;
  if (isLiteral([def.repository, def.registryAuth, def.insecureRegistry]))
    checkRegistryHost(
      def.name,
      def.repository as string,
      push,
      def.registryAuth !== undefined,
      def.insecureRegistry === true,
    );
  const secrets = [
    ...(authSecret ? [authSecret.password] : []),
    ...(def.secrets ?? []),
  ].filter((s, i, all) => all.findIndex((x) => x.name === s.name) === i);
  const registryMap = def.registryMap ?? DEFAULT_REGISTRY_MAP;
  const mapped = isConfigRef(registryMap) || Object.keys(registryMap).length > 0;
  const contract = step({
    name: def.name,
    description: def.description,
    kind: 'imageBuild',
    image: KANIKO_IMAGE,
    input,
    output: push ? PushedImage : BuiltImage,
    secrets,
    timeoutSeconds: def.timeoutSeconds,
    retries: def.retries ?? DEFAULT_RETRIES,
    settings: {
      repository: def.repository,
      ...(context ? { context } : {}),
      ...(push ? { push: true } : {}),
      ...(contentTag ? { contentTag: true } : {}),
      ...(mapped ? { registryMap } : {}),
      ...(authSecret
        ? {
            registryAuth: {
              username: authSecret.username,
              password: authSecret.password.name,
            },
          }
        : {}),
      ...(def.registryAuth && isConfigRef(def.registryAuth)
        ? { registryAuth: def.registryAuth }
        : {}),
      ...(def.insecureRegistry !== undefined && def.insecureRegistry !== false
        ? { insecureRegistry: def.insecureRegistry }
        : {}),
      ...(def.enabled !== undefined ? { enabled: def.enabled } : {}),
    },
  });
  return implement(contract, async ({ source }, ctx: Ctx) => {
    const d = resolveConfigRefs(
      {
        repository: def.repository,
        registryMap: def.registryMap,
        insecureRegistry: def.insecureRegistry,
        registryAuth: def.registryAuth,
        enabled: def.enabled,
      },
      ctx.config ?? {},
    );
    const repository = d.repository as string;
    if (d.enabled === false) return ctx.skip(`publishing ${repository} is turned off`);
    const insecure = d.insecureRegistry === true;
    const auth = registryLogin(
      d.registryAuth as RegistryAuth<string> | RegistryAuthConfig | undefined,
      ctx,
      secrets.map((s) => s.name),
    );
    checkRegistryHost(def.name, repository, push, auth !== undefined, insecure);
    const host = registryHost(repository);

    const tree = await ctx.files.fetch(source.tree);
    const contextDir = context ? join(tree, context) : tree;
    if (!existsSync(contextDir) || !statSync(contextDir).isDirectory())
      throw new Error(`build context '${context}' not found in the source tree`);
    if (!existsSync(join(contextDir, 'Dockerfile')))
      throw new Error(
        `Dockerfile not found in the build context${context ? ` '${context}'` : ''}`,
      );

    const access: RegistryAccess = { insecure, ...(auth ?? {}) };
    if (auth)
      writeDockerConfig(
        process.env.DOCKER_CONFIG || KANIKO_DOCKER_CONFIG,
        host,
        auth.username,
        auth.password,
      );

    const tag = contentTag ? contentTagOf(contextDir, ctx.tmpDir) : imageTag(ctx.run);
    if (contentTag) {
      const digest = await registryDigest(ctx.http, repository, tag, access);
      if (digest) {
        ctx.log('image already in the registry', {
          image: `${repository}:${tag}`,
          digest,
        });
        return { repository, tag, digest };
      }
    }

    const mirrors = await reachableMirrors(
      ctx,
      (d.registryMap as Record<string, string> | undefined) ?? DEFAULT_REGISTRY_MAP,
      insecure ? [host] : [],
    );
    await ctx.exec(['mkdir', '-p', KANIKO_CONTEXT]);
    await ctx.exec(['cp', '-r', `${tree}/.`, KANIKO_CONTEXT]);
    const kanikoContext = context ? `${KANIKO_CONTEXT}/${context}` : KANIKO_CONTEXT;

    // Outputs go to the harness's scratch dir (/olve/tmp, an emptyDir mount kaniko leaves alone;
    // kaniko images have no /tmp), then into the step's files.
    mkdirSync(ctx.tmpDir, { recursive: true });
    const out = mkdtempSync(join(ctx.tmpDir, 'image-build-'));
    const tarPath = join(out, 'image.tar');
    const digestPath = join(out, 'digest');
    const output = push ? [] : ['--no-push', `--tar-path=${tarPath}`];
    // No --single-snapshot: it is incompatible with multi-stage Dockerfiles whose later stages need
    // the build context intact.
    await ctx.exec(
      [
        '/kaniko/executor',
        `--context=${kanikoContext}`,
        `--dockerfile=${kanikoContext}/Dockerfile`,
        ...output,
        `--digest-file=${digestPath}`,
        `--destination=${repository}:${tag}`,
        ...(insecure ? ['--insecure', `--insecure-registry=${host}`] : []),
        '--snapshot-mode=redo',
        '--use-new-run',
        '--compressed-caching=false',
        ...Object.entries(mirrors).map(([from, to]) => `--registry-map=${from}=${to}`),
      ],
      { idleTimeoutSeconds: def.stallTimeoutSeconds ?? DEFAULT_STALL_TIMEOUT_SECONDS },
    );

    let digest: string;
    try {
      digest = readFileSync(digestPath, 'utf8').trim();
    } catch {
      throw new Error(`kaniko did not write the image digest to ${digestPath}`);
    }
    if (!/^sha256:[0-9a-f]{64}$/.test(digest))
      throw new Error(`kaniko wrote an unexpected digest: '${digest}'`);
    if (push) {
      ctx.log('image pushed', { image: `${repository}:${tag}`, digest });
      return { repository, tag, digest };
    }
    const tar = await ctx.output.file(tarPath, { name: 'image.tar' });
    rmSync(tarPath); // the copy in the step's files is the one that counts; don't hold the image twice in /olve
    ctx.log('image built', { image: `${repository}:${tag}`, digest });
    return { repository: qualifyRepository(repository), tag, digest, tar };
  }) as never;
}

function checkRegistryHost(
  name: string,
  repository: string,
  push: boolean,
  auth: boolean,
  insecure: boolean,
): void {
  const host = registryHost(repository);
  if (push && !/[.:]|^localhost$/.test(host))
    throw new Error(
      `imageBuild '${name}': push needs a repository with a registry host, got '${repository}'`,
    );
  if ((auth || insecure) && !/[.:]|^localhost$/.test(host))
    throw new Error(
      `imageBuild '${name}': registryAuth and insecureRegistry need a repository with a registry host, got '${repository}'`,
    );
}

/** The login for `registryAuth` (a literal secret or a config `secretRef`), or undefined without one. */
function registryLogin(
  auth: RegistryAuth<string> | RegistryAuthConfig | undefined,
  ctx: Pick<Ctx, 'secrets'>,
  declared: readonly string[],
): { username: string; password: string } | undefined {
  if (!auth) return undefined;
  const name = 'name' in auth.password ? auth.password.name : auth.password.secret;
  if (!declared.includes(name))
    throw new Error(
      `registryAuth names secret ${name}, which the step does not declare (add it to secrets)`,
    );
  const password = (ctx.secrets as Record<string, string | undefined>)[name];
  if (!password) throw new Error(`secret ${name} (registryAuth) is not set`);
  return { username: auth.username, password };
}

type AnyImageBuild = Implementation<
  Step<string, ImageBuildInput, typeof BuiltImage | typeof PushedImage, any>
>;
