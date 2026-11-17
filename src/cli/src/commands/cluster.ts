import { randomBytes } from 'node:crypto';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { CliError } from '../errors';
import { diagnostic, succeeded, type ProcessResult } from '../process';
import type { Command, CommandContext } from '../registry';

export const PROD_NAMESPACE = 'apps';
const DEFAULT_RELEASE = 'olve-pipelines';
const DEFAULT_BUCKET = 'olve-pipelines';
export const CHART = 'oci://ghcr.io/olivervea/charts/olve-pipelines';
// The name predates Garage; kept so existing installs converge.
const STORAGE_SECRET = 'olve-pipelines-minio';
const STORAGE_ACCESS_KEY = 'olve-pipelines';

const step = (ctx: CommandContext, message: string) => ctx.out.line(`==> ${message}`);

async function checked(
  ctx: CommandContext,
  command: string,
  args: string[],
): Promise<ProcessResult> {
  const r = await ctx.run({ command, args }, ctx.signal);
  // Only the subcommand: the arguments can hold generated credentials.
  if (!succeeded(r))
    throw new CliError(
      `'${command} ${args.slice(0, 2).join(' ')}' failed (exit ${r.exitCode}): ${diagnostic(r)}`,
    );
  return r;
}

function targetNamespace(ctx: CommandContext, action: string): string {
  const ns = ctx.args.option('namespace')?.trim();
  if (!ns) throw new CliError("'--namespace' (-n) is required.");
  if (ns === PROD_NAMESPACE && !ctx.args.flag('allow-prod'))
    throw new CliError(
      `Refusing to ${action} the prod namespace '${ns}' without --allow-prod.`,
    );
  return ns;
}

async function preflight(ctx: CommandContext): Promise<void> {
  step(ctx, 'Preflight: checking kubectl, helm, and cluster access');
  await checked(ctx, 'kubectl', ['version', '--client']);
  await checked(ctx, 'helm', ['version', '--short']);
  const context = await ctx.run(
    { command: 'kubectl', args: ['config', 'current-context'] },
    ctx.signal,
  );
  if (succeeded(context)) ctx.out.line(`  context: ${context.stdout.trim()}`);
  const info = await ctx.run({ command: 'kubectl', args: ['cluster-info'] }, ctx.signal);
  if (!succeeded(info))
    throw new CliError(`Cluster is not reachable: ${diagnostic(info)}`);
}

const hex = (bytes: number) => randomBytes(bytes).toString('hex');

async function ensureStorageSecret(ctx: CommandContext, ns: string): Promise<void> {
  const get = await ctx.run(
    { command: 'kubectl', args: ['get', 'secret', STORAGE_SECRET, '-n', ns] },
    ctx.signal,
  );
  if (!succeeded(get)) {
    // Generate only when absent: the cluster Secret is the source of truth, and a re-run must not rotate a running Garage's key.
    step(ctx, `Generating storage credentials secret '${STORAGE_SECRET}'`);
    await checked(ctx, 'kubectl', [
      'create',
      'secret',
      'generic',
      STORAGE_SECRET,
      '-n',
      ns,
      `--from-literal=root-user=${STORAGE_ACCESS_KEY}`,
      `--from-literal=root-password=${hex(24)}`,
      `--from-literal=rpc-secret=${hex(32)}`,
    ]);
    return;
  }
  // Secrets from before Garage lack its cluster RPC secret: add only that key.
  const rpc = await checked(ctx, 'kubectl', [
    'get',
    'secret',
    STORAGE_SECRET,
    '-n',
    ns,
    '-o',
    'jsonpath={.data.rpc-secret}',
  ]);
  if (rpc.stdout.trim()) {
    step(
      ctx,
      `Storage credentials secret '${STORAGE_SECRET}' already exists — leaving untouched`,
    );
    return;
  }
  step(ctx, `Adding Garage rpc-secret to existing secret '${STORAGE_SECRET}'`);
  await checked(ctx, 'kubectl', [
    'patch',
    'secret',
    STORAGE_SECRET,
    '-n',
    ns,
    '--type',
    'merge',
    '-p',
    JSON.stringify({ stringData: { 'rpc-secret': hex(32) } }),
  ]);
}

/** The chart version a release published: its version stamp as SemVer (2026.01.05.12 → 20260105.12.0). */
export function chartVersion(release: string): string | undefined {
  const m = /^(\d{4})\.(\d{2})\.(\d{2})\.(\d+)$/.exec(release);
  return m ? `${m[1]}${m[2]}${m[3]}.${Number(m[4])}.0` : undefined;
}

type ChartSource = { dir: string } | { release: string; version: string };

/** The chart: a local directory, or the published chart of a release (`--version`, else this pl's). */
function chartSource(ctx: CommandContext): ChartSource {
  const local = ctx.args.option('chart');
  const pinned = ctx.args.option('version')?.trim();
  if (local !== undefined) {
    if (pinned) throw new CliError("'--chart' and '--version' exclude each other.");
    const dir = resolve(local);
    if (!existsSync(dir)) throw new CliError(`Chart directory '${dir}' does not exist.`);
    if (!existsSync(join(dir, 'Chart.yaml')))
      throw new CliError(`'${dir}' is not a helm chart (no Chart.yaml).`);
    return { dir };
  }
  const release = pinned || ctx.version;
  const version = chartVersion(release);
  if (!version)
    throw new CliError(
      pinned
        ? `'--version' takes a release version like 2026.11.17.296; got '${pinned}'.`
        : `This pl (${release}) is not a release, so it names no chart: pass --version <release> (one of the GitHub releases, e.g. 2026.11.17.296) or --chart <dir>.`,
    );
  return { release, version };
}

async function fetchChart(
  ctx: CommandContext,
  source: ChartSource,
): Promise<{ dir: string; temp?: string }> {
  if ('dir' in source) return source;
  const { release, version } = source;
  step(ctx, `Pulling chart ${CHART} ${version} (release ${release})`);
  const temp = mkdtempSync(join(tmpdir(), 'pl-chart-'));
  try {
    const pull = await ctx.run(
      {
        command: 'helm',
        args: ['pull', CHART, '--version', version, '--untar', '--untardir', temp],
      },
      ctx.signal,
    );
    if (!succeeded(pull))
      throw new CliError(
        `Could not pull chart ${CHART} ${version} (release ${release}): ${diagnostic(pull)}`,
      );
    const dir = join(temp, 'olve-pipelines');
    if (!existsSync(join(dir, 'Chart.yaml')))
      throw new CliError(`Pulling ${CHART} ${version} gave no chart directory.`);
    return { dir, temp };
  } catch (error) {
    rmSync(temp, { recursive: true, force: true });
    throw error;
  }
}

async function rollout(
  ctx: CommandContext,
  ns: string,
  deployment: string,
  timeout: string,
): Promise<void> {
  await checked(ctx, 'kubectl', [
    '-n',
    ns,
    'rollout',
    'status',
    `deploy/${deployment}`,
    `--timeout=${timeout}`,
  ]);
}

export const clusterCommands: Command[] = [
  {
    noun: 'bootstrap',
    usesApi: false,
    options: [
      'bucket',
      'chart',
      'image-pull-policy',
      'image-repository',
      'image-tag',
      'namespace',
      'release',
      'version',
    ],
    flags: ['allow-prod'],
    aliases: { n: 'namespace' },
    summary: 'Cold-install the controller + private Garage store (idempotent)',
    detail: `pl bootstrap -n <namespace> [options]   Cold-install the controller + private Garage store
  -n, --namespace <ns>     Target namespace (required)
  --release <name>         Helm release name (default: olve-pipelines)
  --bucket <name>          Storage bucket name (default: olve-pipelines)
  --version <release>      Release whose published chart to install, e.g. 2026.11.17.296 (default: this pl's)
  --chart <path>           Install a local chart directory instead (a checkout's src/deploy/helm)
  --image-tag <tag>        Controller image tag (default: the chart's release; latest for a checkout)
  --image-repository <r>   Controller image repository (default: chart value)
  --image-pull-policy <p>  Controller image pull policy, e.g. Never (default: chart value)
  --allow-prod             Permit installing into the prod namespace 'apps'`,
    async run(ctx) {
      const ns = targetNamespace(ctx, 'bootstrap into');
      const release = ctx.args.optionOr('release', DEFAULT_RELEASE);
      const bucket = ctx.args.optionOr('bucket', DEFAULT_BUCKET);
      const source = chartSource(ctx);
      await preflight(ctx);
      const chart = await fetchChart(ctx, source);
      try {
        const nsGet = await ctx.run(
          { command: 'kubectl', args: ['get', 'namespace', ns] },
          ctx.signal,
        );
        if (!succeeded(nsGet)) {
          step(ctx, `Creating namespace '${ns}'`);
          await checked(ctx, 'kubectl', ['create', 'namespace', ns]);
        }
        await ensureStorageSecret(ctx, ns);
        step(ctx, `Deploying release '${release}' (minimal profile) into '${ns}'`);
        const args = [
          'upgrade',
          '--install',
          release,
          chart.dir,
          '-n',
          ns,
          '-f',
          join(chart.dir, 'values-minimal.yaml'),
          '--set',
          `config.Storage__Endpoint=http://${release}-garage.${ns}:3900`,
          '--set',
          `config.Storage__Bucket=${bucket}`,
          '--set',
          `config.Kubernetes__Namespace=${ns}`,
          '--set',
          `garage.bucket=${bucket}`,
        ];
        const tag = ctx.args.option('image-tag')?.trim();
        if (tag) args.push('--set', `image.tag=${tag}`);
        const repository = ctx.args.option('image-repository')?.trim();
        if (repository) args.push('--set', `image.repository=${repository}`);
        const pullPolicy = ctx.args.option('image-pull-policy')?.trim();
        if (pullPolicy) args.push('--set', `image.pullPolicy=${pullPolicy}`);
        await checked(ctx, 'helm', args);
        // Garage creates the access key and bucket itself at startup.
        step(ctx, `Waiting for Garage (${release}-garage) to be ready`);
        await rollout(ctx, ns, `${release}-garage`, '120s');
        step(ctx, `Waiting for the controller (${release}) to become ready`);
        await rollout(ctx, ns, release, '180s');
      } finally {
        if (chart.temp) rmSync(chart.temp, { recursive: true, force: true });
      }
      step(ctx, `Done. Controller '${release}' is ready in namespace '${ns}'.`);
    },
  },
  {
    noun: 'teardown',
    usesApi: false,
    options: ['namespace', 'release'],
    flags: ['allow-prod', 'purge-data'],
    aliases: { n: 'namespace' },
    summary: 'Remove an installation (release + storage creds; data PVCs retained)',
    detail: `pl teardown -n <namespace> [options]   Remove an installation
  -n, --namespace <ns>   Target namespace (required)
  --release <name>       Helm release name (default: olve-pipelines)
  --purge-data           Also delete the storage data PVCs (full wipe)`,
    async run(ctx) {
      const ns = targetNamespace(ctx, 'tear down');
      const release = ctx.args.optionOr('release', DEFAULT_RELEASE);
      const purge = ctx.args.flag('purge-data');
      await preflight(ctx);
      const status = await ctx.run(
        { command: 'helm', args: ['status', release, '-n', ns] },
        ctx.signal,
      );
      if (!succeeded(status))
        step(ctx, `Release '${release}' not found in '${ns}' — already removed`);
      else {
        step(ctx, `Uninstalling release '${release}' from '${ns}'`);
        await checked(ctx, 'helm', ['uninstall', release, '-n', ns]);
      }
      // bootstrap creates the Secret with kubectl, so helm uninstall leaves it.
      step(ctx, `Deleting storage credentials secret '${STORAGE_SECRET}'`);
      await checked(ctx, 'kubectl', [
        'delete',
        'secret',
        STORAGE_SECRET,
        '-n',
        ns,
        '--ignore-not-found',
      ]);
      if (purge) {
        const pvcs = [`${release}-garage-meta`, `${release}-garage-data`];
        step(ctx, `Purging storage data volumes ${pvcs.join(', ')}`);
        await checked(ctx, 'kubectl', [
          'delete',
          'pvc',
          ...pvcs,
          '-n',
          ns,
          '--ignore-not-found',
        ]);
      }
      step(
        ctx,
        `Done. Release '${release}' removed from '${ns}'${purge ? ' (storage data purged).' : ' (storage data PVCs retained).'}`,
      );
    },
  },
];
