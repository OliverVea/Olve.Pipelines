// steps.staticSite: a directory of static files → a Caddy image → a Helm release of the bundled chart, to a
// deploy target or as a pull request's preview.
import {
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join } from 'node:path';
import { z } from 'zod';
import { CiInput } from '../builder/checks.ts';
import type { Ctx } from '../ctx/types.ts';
import type { ConfigRef, Configurable } from '../core/config.ts';
import { resolveConfigRefs } from '../core/config-refs.ts';
import { implement, type Implementation } from '../core/handlers.ts';
import type { Secret } from '../core/secrets.ts';
import { step, type LiteralName, type Step } from '../core/steps.ts';
import {
  HELM_IMAGE,
  targetDeploy,
  type InClusterTargetValue,
  type SshTargetValue,
} from './helm-deploy.ts';
import {
  DEFAULT_RETRIES,
  imageBuilder,
  KANIKO_IMAGE,
  type RegistryAuth,
  type RegistryAuthConfig,
} from './image-build.ts';
import { httpHealthCheck } from './http-health.ts';
import { HelmRelease, PagesDeployment, PushedImage, Site } from './models.ts';
import {
  PAGES_NOT_FOUND,
  pagesHeaders,
  SITE_CHART_NAME,
  siteCaddyfile,
  siteDockerfile,
  writeSiteChart,
} from './static-site-assets.ts';

export interface StaticSiteDef<N extends string, K extends string = never> {
  /** Prefix of the step names: `<name>-image`, `<name>-preview`, `<name>-preview-remove`. */
  name: LiteralName<N>;
  /** Registry repository the image is pushed to (`registry.example.com/web`); deploys pin it by digest. */
  repository: Configurable<string>;
  /** kaniko `--registry-map` for the Caddy base image, as in `imageBuild`. */
  registryMap?: Configurable<Record<string, string> | undefined>;
  /** Login for the registry of `repository`, as in `imageBuild`. */
  registryAuth?: RegistryAuth<K> | ConfigRef<RegistryAuthConfig | undefined>;
  /** Secrets a config-referenced `registryAuth` may name. */
  secrets?: readonly Secret<K>[];
  /** Adds `'wasm-unsafe-eval'` to the CSP's `script-src` (a WebAssembly app). Default false. */
  wasm?: boolean;
  /** Answer unknown paths with `/index.html` (a single-page app's client-side routes). Default false. */
  spa?: boolean;
  /** The site's directory inside `Site.root` (e.g. `site` when the root is a source tree). Default: the root. */
  dir?: string;
  /** The image step's timeout. */
  timeoutSeconds?: number;
}

/** An ingress for a deploy: rendered when `host` is set; `tlsSecret` names the certificate Secret. */
export const siteIngress = () =>
  z.object({
    host: z.string().describe('The host the Ingress routes to the site.'),
    tlsSecret: z
      .string()
      .optional()
      .describe("The TLS certificate's Secret in the release namespace; unset = no TLS."),
    className: z
      .string()
      .optional()
      .describe("The IngressClass; unset = the cluster's default."),
    annotations: z
      .record(z.string(), z.string())
      .optional()
      .describe('Annotations on the Ingress (cert-manager, external-dns).'),
  });
export type SiteIngress = z.output<ReturnType<typeof siteIngress>>;

interface SiteDeployCommon<D extends string> {
  name: LiteralName<D>;
  /** What the step does, shown in the UI. Default: a sentence about the deploy. */
  description?: string;
  /** An Ingress for the release, usually a config reference (`WEB.prod.ingress`); absent = none. */
  ingress?: Configurable<SiteIngress | undefined>;
  /** After the rollout, GET `<target url><healthPath>` until it returns 2xx. */
  healthPath?: string;
  rolloutTimeoutSeconds?: number;
  timeoutSeconds?: number;
}

/** A deploy to an in-cluster target: helm runs in the step pod. */
export interface SiteDeployInClusterDef<D extends string> extends SiteDeployCommon<D> {
  /** Usually a `select` case's reference (`inCluster: (p, beta) => … target: beta`). */
  target: Configurable<InClusterTargetValue>;
  sshKey?: undefined;
}

/** A deploy to an ssh target: helm runs on the k3s host. */
export interface SiteDeploySshDef<
  D extends string,
  K extends string,
> extends SiteDeployCommon<D> {
  target: Configurable<SshTargetValue>;
  /** Private key for the target's host. */
  sshKey: Secret<K>;
}

export interface SitePreviewDef<K extends string> {
  /** A kubeconfig allowed to install releases in `namespace` (written to a file, never on a command line). */
  kubeconfig: Secret<K>;
  namespace: Configurable<string>;
  /** Each pull request's preview is served at `pr-<number>.<domain>`. */
  domain: Configurable<string>;
  /** The certificate Secret for the hosts (a wildcard for `*.<domain>`); unset = no TLS. */
  tlsSecret?: Configurable<string | undefined>;
  /** The IngressClass; unset = the cluster's default. */
  className?: Configurable<string | undefined>;
  annotations?: Configurable<Record<string, string> | undefined>;
  /**
   * Release name prefix: a preview's release is `<release>-<pull>`. Default `<repository name>-<site name>-preview`,
   * so pipelines sharing a namespace don't collide.
   */
  release?: string;
  /** helm's `--wait` timeout for a preview. Default 300. */
  rolloutTimeoutSeconds?: number;
  timeoutSeconds?: number;
}

/** A deploy to Cloudflare Pages: the site's files are uploaded as the project's production deploy. */
export interface SitePagesDef<D extends string, K extends string> {
  name: LiteralName<D>;
  /** What the step does, shown in the UI. Default: a sentence about the deploy. */
  description?: string;
  /** The Cloudflare account that owns the project. */
  accountId: Configurable<string>;
  /** The Pages project; created on the first deploy when it doesn't exist. */
  project: Configurable<string>;
  /** A Cloudflare API token with Account → Cloudflare Pages → Edit on the account. */
  apiToken: Secret<K>;
  /** The site's public URL (its custom domain), linked from the step; unset = the project's `pages.dev` URL. */
  link?: Configurable<string | undefined>;
  timeoutSeconds?: number;
}

type SiteImageInput = { readonly site: typeof Site };
type SiteDeployInput = { readonly image: typeof PushedImage };
type PreviewInput = { readonly image: typeof PushedImage; readonly ci: typeof CiInput };
type RemoveInput = { readonly ci: typeof CiInput };

export interface SitePreview<N extends string, K extends string> {
  /** Installs the pull request's preview (`reuse: false`) and links it. Stage it in `when({ kind: 'pull' }, …)`. */
  deploy: Implementation<Step<`${N}-preview`, PreviewInput, typeof HelmRelease, K>>;
  /** Uninstalls it; stage it in `onClose`. */
  remove: Implementation<Step<`${N}-preview-remove`, RemoveInput, null, K>>;
}

export interface StaticSite<N extends string, K extends string> {
  /** Builds the site's image with kaniko and pushes it: `Site` in, `PushedImage` out. */
  image: Implementation<Step<`${N}-image`, SiteImageInput, typeof PushedImage, K>>;
  /** A Helm deploy of the image with the bundled chart. `sshKey` goes with an ssh target. */
  deploy<const D extends string>(
    def: SiteDeployInClusterDef<D>,
  ): Implementation<Step<D, SiteDeployInput, typeof HelmRelease, never>>;
  deploy<const D extends string, const S extends string>(
    def: SiteDeploySshDef<D, S>,
  ): Implementation<Step<D, SiteDeployInput, typeof HelmRelease, S>>;
  /** A pull request's preview and its removal, for `checks()`. */
  preview<const P extends string>(def: SitePreviewDef<P>): SitePreview<N, P>;
  /** Uploads the site to Cloudflare Pages with wrangler: `Site` in, `PagesDeployment` out. No image is built. */
  pages<const D extends string, const P extends string>(
    def: SitePagesDef<D, P>,
  ): Implementation<Step<D, SiteImageInput, typeof PagesDeployment, P>>;
}

/** Default `rolloutTimeoutSeconds` of a preview. */
export const PREVIEW_ROLLOUT_TIMEOUT_SECONDS = 300;

const DNS_LABEL = /^[a-z0-9]([-a-z0-9]*[a-z0-9])?$/;
/** Helm's release name limit. */
const MAX_RELEASE = 53;

/** The default release prefix: `<repository name>-<site name>-preview`, as a DNS label. */
export function defaultPreviewPrefix(repo: string, site: string): string {
  const name = repo.split('/').at(-1)!;
  return `${name}-${site}-preview`
    .toLowerCase()
    .replace(/[^a-z0-9-]+/g, '-')
    .replace(/-+/g, '-')
    .replace(/^-|-$/g, '');
}

/** A preview's release name. */
export function previewRelease(prefix: string, pull: number): string {
  const release = `${prefix}-${pull}`;
  if (release.length > MAX_RELEASE || !DNS_LABEL.test(release))
    throw new Error(
      `preview release '${release}' is not a Helm release name (lowercase letters, digits and '-', at most ${MAX_RELEASE} characters); set release`,
    );
  return release;
}

function checkDir(name: string, dir: string): string {
  const clean = dir.replace(/^\.\/+/, '').replace(/\/+$/, '');
  if (clean.startsWith('/') || clean.split('/').includes('..'))
    throw new Error(
      `staticSite '${name}': dir '${dir}' must be a relative path inside the site root`,
    );
  return clean === '.' ? '' : clean;
}

const helmEnv = (ctx: Pick<Ctx, 'tmpDir'>, extra: Record<string, string> = {}) => {
  const home = join(ctx.tmpDir, 'helm-home');
  return {
    HOME: home,
    HELM_CACHE_HOME: join(home, 'cache'),
    HELM_CONFIG_HOME: join(home, 'config'),
    HELM_DATA_HOME: join(home, 'data'),
    ...extra,
  };
};

function writeKubeconfig(ctx: Pick<Ctx, 'tmpDir'>, value: string): string {
  mkdirSync(ctx.tmpDir, { recursive: true });
  const file = join(mkdtempSync(join(ctx.tmpDir, 'kube-')), 'config');
  writeFileSync(file, value, { mode: 0o600 });
  return file;
}

/**
 * A static site: `image` builds a directory of files (`Site`) into a Caddy-on-scratch image and pushes it,
 * `deploy` installs it on a Helm target with the bundled chart, `preview` deploys a pull request's copy.
 */
export function staticSite<const N extends string, const K extends string = never>(
  def: StaticSiteDef<N, K>,
): StaticSite<N, K> {
  const dir = checkDir(def.name, def.dir ?? '');
  const wasm = def.wasm === true;
  const spa = def.spa === true;
  const builder = imageBuilder({
    name: `${def.name}-image`,
    repository: def.repository,
    push: true,
    ...(def.registryMap !== undefined ? { registryMap: def.registryMap } : {}),
    ...(def.registryAuth !== undefined ? { registryAuth: def.registryAuth } : {}),
    ...(def.secrets !== undefined ? { secrets: def.secrets } : {}),
  });
  const imageContract = step({
    name: `${def.name}-image` as LiteralName<`${N}-image`>,
    description:
      "Builds the site's image (Caddy on an empty image, with the site) with kaniko and pushes it.",
    kind: 'imageBuild',
    image: KANIKO_IMAGE,
    input: { site: Site },
    output: PushedImage,
    secrets: builder.secrets as readonly Secret<K>[],
    timeoutSeconds: def.timeoutSeconds,
    retries: DEFAULT_RETRIES,
    settings: {
      ...builder.settings,
      site: { ...(dir ? { dir } : {}), wasm, spa },
    },
  });
  const image = implement(
    imageContract,
    async ({ site }, ctx: Ctx) =>
      builder.build(ctx, async () => {
        const root = await ctx.files.fetch(site.root);
        const from = dir ? join(root, dir) : root;
        if (!existsSync(from) || !statSync(from).isDirectory())
          throw new Error(`'${dir}' is not a directory of the site root`);
        if (!existsSync(join(from, 'index.html')))
          throw new Error(`the site has no index.html${dir ? ` (in '${dir}')` : ''}`);
        mkdirSync(ctx.tmpDir, { recursive: true });
        const context = mkdtempSync(join(ctx.tmpDir, 'site-'));
        writeFileSync(join(context, 'Dockerfile'), siteDockerfile());
        writeFileSync(join(context, 'Caddyfile'), siteCaddyfile({ wasm, spa }));
        cpSync(from, join(context, 'wwwroot'), { recursive: true });
        return context;
      }) as never,
  ) as never;

  const deploy = (d: SiteDeployCommon<string> & { target: unknown; sshKey?: Secret }) =>
    targetDeploy(
      d.sshKey ? 'helmDeployOverSsh' : 'helmDeployInCluster',
      {
        name: d.name,
        description: d.description ?? 'Deploys the static site with Helm.',
        chart: SITE_CHART_NAME,
        target: d.target,
        ...(d.ingress !== undefined ? { inlineValues: [{ ingress: d.ingress }] } : {}),
        images: { image: 'image' },
        ...(d.healthPath !== undefined ? { healthPath: d.healthPath } : {}),
        ...(d.rolloutTimeoutSeconds !== undefined
          ? { rolloutTimeoutSeconds: d.rolloutTimeoutSeconds }
          : {}),
        ...(d.timeoutSeconds !== undefined ? { timeoutSeconds: d.timeoutSeconds } : {}),
      },
      d.sshKey,
      undefined,
      { name: SITE_CHART_NAME, write: writeSiteChart },
    );

  const preview = (p: SitePreviewDef<string>): SitePreview<N, string> => {
    if (p.release !== undefined && (!DNS_LABEL.test(p.release) || p.release.length > 42))
      throw new Error(
        `staticSite '${def.name}': preview release '${p.release}' must be a DNS label of at most 42 characters`,
      );
    const prefixOf = (ci: { repo: string }) =>
      p.release ?? defaultPreviewPrefix(ci.repo, def.name);
    const rollout = p.rolloutTimeoutSeconds ?? PREVIEW_ROLLOUT_TIMEOUT_SECONDS;
    const configurable = {
      namespace: p.namespace,
      domain: p.domain,
      tlsSecret: p.tlsSecret,
      className: p.className,
      annotations: p.annotations,
    };
    const deployContract = step({
      name: `${def.name}-preview` as LiteralName<string>,
      description:
        "Deploys the pull request's preview of the site with Helm and links it.",
      kind: 'staticSitePreview',
      image: HELM_IMAGE,
      input: { image: PushedImage, ci: CiInput },
      output: HelmRelease,
      secrets: [p.kubeconfig],
      reuse: false,
      timeoutSeconds: p.timeoutSeconds ?? 600,
      settings: {
        namespace: p.namespace,
        domain: p.domain,
        ...(p.tlsSecret !== undefined ? { tlsSecret: p.tlsSecret } : {}),
        ...(p.className !== undefined ? { className: p.className } : {}),
        ...(p.annotations !== undefined ? { annotations: p.annotations } : {}),
        ...(p.release !== undefined ? { release: p.release } : {}),
        ...(p.rolloutTimeoutSeconds !== undefined
          ? { rolloutTimeoutSeconds: p.rolloutTimeoutSeconds }
          : {}),
      },
    });
    const deploy = implement(deployContract, async ({ image, ci }, ctx: Ctx) => {
      if (ci.kind !== 'pull')
        throw new Error(
          `a preview is deployed by a pull request's run, not a ${ci.kind} run (stage it in when({ kind: 'pull' }, …))`,
        );
      const v = resolveConfigRefs(configurable, ctx.config ?? {}) as {
        namespace: string;
        domain: string;
        tlsSecret?: string;
        className?: string;
        annotations?: Record<string, string>;
      };
      const domain = v.domain.replace(/^\.+|\.+$/g, '');
      if (!/^[a-z0-9]([-a-z0-9.]*[a-z0-9])?$/i.test(domain))
        throw new Error(`domain '${v.domain}' is not a host name`);
      const prefix = prefixOf(ci);
      const release = previewRelease(prefix, ci.pull);
      const host = `pr-${ci.pull}.${domain}`;
      mkdirSync(ctx.tmpDir, { recursive: true });
      const chartDir = writeSiteChart(mkdtempSync(join(ctx.tmpDir, 'chart-')));
      const values: string[] = [];
      if (v.annotations && Object.keys(v.annotations).length > 0) {
        writeFileSync(
          join(chartDir, 'olve-preview-values.json'),
          JSON.stringify({ ingress: { annotations: v.annotations } }),
        );
        values.push('-f', 'olve-preview-values.json');
      }
      const set = (k: string, val: string) => ['--set-string', `${k}=${val}`];
      ctx.log('helm upgrade', { release, namespace: v.namespace, host });
      await ctx.exec(
        [
          'helm',
          'upgrade',
          '--install',
          release,
          '.',
          '-n',
          v.namespace,
          ...values,
          ...set('image.repository', image.repository),
          ...set('image.tag', image.tag),
          ...set('image.digest', image.digest),
          ...set('preview', prefix),
          ...set('ingress.host', host),
          ...(v.tlsSecret ? set('ingress.tlsSecret', v.tlsSecret) : []),
          ...(v.className ? set('ingress.className', v.className) : []),
          '--wait',
          `--timeout=${rollout}s`,
        ],
        {
          cwd: chartDir,
          env: helmEnv(ctx, {
            KUBECONFIG: writeKubeconfig(ctx, kubeconfigOf(ctx, p.kubeconfig)),
          }),
        },
      );
      const url = `${v.tlsSecret ? 'https' : 'http'}://${host}`;
      ctx.link(url, 'preview');
      return {
        release,
        namespace: v.namespace,
        image: `${image.repository}:${image.tag}@${image.digest}`,
        url,
      };
    });

    const removeContract = step({
      name: `${def.name}-preview-remove` as LiteralName<string>,
      description: "Removes the pull request's preview of the site.",
      kind: 'staticSitePreviewRemove',
      image: HELM_IMAGE,
      input: { ci: CiInput },
      secrets: [p.kubeconfig],
      reuse: false,
      timeoutSeconds: 300,
      settings: {
        namespace: p.namespace,
        ...(p.release !== undefined ? { release: p.release } : {}),
      },
    });
    const remove = implement(removeContract, async ({ ci }, ctx: Ctx) => {
      const { namespace } = resolveConfigRefs(
        { namespace: p.namespace },
        ctx.config ?? {},
      ) as {
        namespace: string;
      };
      const release = previewRelease(prefixOf(ci), ci.pull);
      ctx.log('helm uninstall', { release, namespace });
      await ctx.exec(
        ['helm', 'uninstall', release, '-n', namespace, '--ignore-not-found', '--wait'],
        {
          env: helmEnv(ctx, {
            KUBECONFIG: writeKubeconfig(ctx, kubeconfigOf(ctx, p.kubeconfig)),
          }),
        },
      );
    });
    return { deploy, remove } as never;
  };

  const pages = (p: SitePagesDef<string, string>) => {
    const contract = step({
      name: p.name,
      description:
        p.description ??
        "Uploads the site to Cloudflare Pages as the project's production deploy.",
      kind: 'pagesDeploy',
      image: PAGES_IMAGE,
      input: { site: Site },
      output: PagesDeployment,
      secrets: [p.apiToken],
      timeoutSeconds: p.timeoutSeconds ?? 900,
      retries: DEFAULT_RETRIES,
      settings: {
        accountId: p.accountId,
        project: p.project,
        apiToken: p.apiToken.name,
        ...(p.link !== undefined ? { link: p.link } : {}),
        site: { ...(dir ? { dir } : {}), wasm, spa },
      },
    });
    return implement(contract, async ({ site }, ctx: Ctx) => {
      const v = resolveConfigRefs(
        { accountId: p.accountId, project: p.project, link: p.link },
        ctx.config ?? {},
      ) as { accountId: string; project: string; link?: string };
      const token = (ctx.secrets as Record<string, string | undefined>)[p.apiToken.name];
      if (!token?.trim())
        throw new Error(`secret ${p.apiToken.name} (apiToken) is not set`);

      const root = await ctx.files.fetch(site.root);
      const from = dir ? join(root, dir) : root;
      if (!existsSync(from) || !statSync(from).isDirectory())
        throw new Error(`'${dir}' is not a directory of the site root`);
      if (!existsSync(join(from, 'index.html')))
        throw new Error(`the site has no index.html${dir ? ` (in '${dir}')` : ''}`);
      if (existsSync(join(from, '_headers')))
        throw new Error(
          "the site has its own _headers: the step writes it (set the headers with the step's options)",
        );
      mkdirSync(ctx.tmpDir, { recursive: true });
      const upload = join(mkdtempSync(join(ctx.tmpDir, 'pages-')), 'site');
      // Pages compresses on its own; the precompressed copies Caddy serves would only add files.
      cpSync(from, upload, {
        recursive: true,
        filter: (src) =>
          !(/\.(br|gz)$/.test(src) && existsSync(src.replace(/\.(br|gz)$/, ''))),
      });
      writeFileSync(join(upload, '_headers'), pagesHeaders({ wasm }));
      if (!spa && !existsSync(join(upload, '404.html')))
        writeFileSync(join(upload, '404.html'), PAGES_NOT_FOUND);

      const project = await ensurePagesProject(ctx, v.accountId, v.project, token);
      const home = join(ctx.tmpDir, 'pages-home');
      ctx.log('wrangler pages deploy', { project: v.project });
      await ctx.exec(
        [
          'npx',
          '--yes',
          `wrangler@${WRANGLER_VERSION}`,
          'pages',
          'deploy',
          upload,
          '--project-name',
          v.project,
          '--branch',
          PAGES_PRODUCTION_BRANCH,
          '--commit-dirty=true',
        ],
        {
          cwd: dirname(upload),
          env: {
            CLOUDFLARE_API_TOKEN: token,
            CLOUDFLARE_ACCOUNT_ID: v.accountId,
            HOME: home,
            npm_config_cache: join(home, 'npm'),
            WRANGLER_SEND_METRICS: 'false',
            CI: 'true',
          },
          idleTimeoutSeconds: 300,
        },
      );
      const url = `https://${project.subdomain}`;
      await httpHealthCheck(ctx, url);
      ctx.link(v.link ?? url, 'site');
      return { project: v.project, url };
    });
  };

  return { image, deploy, preview, pages } as never;
}

/** Node on Debian (wrangler's workerd is built for glibc), pinned by digest. */
export const PAGES_IMAGE =
  'node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee';

/** The wrangler the step runs with npx. */
export const WRANGLER_VERSION = '4.156.0';

/** The branch a deploy is made on, which is the project's production branch. */
export const PAGES_PRODUCTION_BRANCH = 'main';

const CLOUDFLARE_API = 'https://api.cloudflare.com/client/v4';

/** Reads the Pages project, creating it with {@link PAGES_PRODUCTION_BRANCH} as production when it doesn't exist. */
async function ensurePagesProject(
  ctx: Pick<Ctx, 'http' | 'log'>,
  accountId: string,
  name: string,
  token: string,
): Promise<{ subdomain: string }> {
  const base = `${CLOUDFLARE_API}/accounts/${encodeURIComponent(accountId)}/pages/projects`;
  const headers = {
    Authorization: `Bearer ${token}`,
    'Content-Type': 'application/json',
  };
  const call = async (what: string, url: string, init: RequestInit) => {
    const res = await ctx.http.fetch(url, { ...init, headers });
    const body = (await res.json().catch(() => ({}))) as {
      result?: { subdomain?: string };
      errors?: { message?: string }[];
    };
    return {
      res,
      body,
      fail: () => {
        const reason = body.errors
          ?.map((e) => e.message)
          .filter(Boolean)
          .join('; ');
        return new Error(
          `Cloudflare Pages: ${what} '${name}' failed with ${res.status}${reason ? `: ${reason}` : ''}`,
        );
      },
    };
  };
  let r = await call('reading project', `${base}/${encodeURIComponent(name)}`, {
    method: 'GET',
  });
  if (r.res.status === 404) {
    ctx.log('creating Pages project', { project: name });
    r = await call('creating project', base, {
      method: 'POST',
      body: JSON.stringify({ name, production_branch: PAGES_PRODUCTION_BRANCH }),
    });
  }
  if (!r.res.ok || !r.body.result?.subdomain) throw r.fail();
  return { subdomain: r.body.result.subdomain };
}

const kubeconfigOf = (ctx: Pick<Ctx, 'secrets'>, secret: Secret): string =>
  (ctx.secrets as Record<string, string>)[secret.name]!;
