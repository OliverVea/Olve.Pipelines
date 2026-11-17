import { cpSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, PushedImage, Source } from '@olivervea/pipelines';
import { PUBLISH, REGISTRY_SECRETS } from '../config';
import { HELM_IMAGE } from '../images';
import { dockerConfig } from '../registry';

/** A release's chart version: its version stamp as SemVer (2026.01.05.12 → 20260105.12.0). */
export function chartVersion(release: string): string {
  const m = /^(\d{4})\.(\d{2})\.(\d{2})\.(\d+)$/.exec(release);
  if (!m) throw new Error(`'${release}' is not a release version (yyyy.MM.dd.<run>)`);
  return `${m[1]}${m[2]}${m[3]}.${Number(m[4])}.0`;
}

/** The OCI registry a release repository's chart goes to: `charts/` beside it (the image owns its name). */
export function chartRegistry(repository: string): string {
  const slash = repository.lastIndexOf('/');
  if (slash < 0)
    throw new Error(`release repository '${repository}' has no registry path`);
  return `oci://${repository.slice(0, slash)}/charts`;
}

export const publishChart = implement(
  step({
    name: 'publish-chart',
    description:
      "Publishes the Helm chart beside each release repository as an OCI chart, versioned as this run's release and defaulting to its server image.",
    image: HELM_IMAGE,
    input: { source: Source, server: PushedImage },
    config: [PUBLISH],
    secrets: REGISTRY_SECRETS,
    timeoutSeconds: 300,
  }),
  async ({ source, server }, ctx) => {
    const releases = ctx.config.publish.server.release;
    if (releases.length === 0) {
      ctx.log('no release repositories (publish.server.release): no chart published');
      return;
    }
    const version = chartVersion(ctx.run.version);
    const tree = await ctx.files.fetch(source.tree);
    const helm = {
      HELM_CACHE_HOME: join(ctx.tmpDir, 'helm/cache'),
      HELM_CONFIG_HOME: join(ctx.tmpDir, 'helm/config'),
      HELM_DATA_HOME: join(ctx.tmpDir, 'helm/data'),
    };
    for (const [i, to] of releases.entries()) {
      const dir = join(ctx.tmpDir, `chart-${i}`, 'olve-pipelines');
      cpSync(join(tree, 'src/deploy/helm'), dir, { recursive: true });
      const values = join(dir, 'values.yaml');
      const text = readFileSync(values, 'utf8');
      const image = /^(image:\n {2}repository: ).*$/m;
      if (!image.test(text))
        throw new Error('src/deploy/helm/values.yaml has no image.repository to set');
      writeFileSync(values, text.replace(image, `$1${to.repository}`));

      const out = join(ctx.tmpDir, `chart-${i}`, 'out');
      await ctx.exec(
        [
          'helm',
          'package',
          dir,
          '--version',
          version,
          '--app-version',
          server.tag,
          '-d',
          out,
        ],
        { env: helm },
      );
      const registry = chartRegistry(to.repository);
      const env = {
        ...helm,
        HELM_REGISTRY_CONFIG: join(
          dockerConfig(ctx, [[to.repository, to]]),
          'config.json',
        ),
      };
      await ctx.exec(
        [
          'helm',
          'push',
          join(out, `olve-pipelines-${version}.tgz`),
          registry,
          ...(to.insecure ? ['--plain-http'] : []),
        ],
        { env },
      );
      ctx.log('published chart', {
        chart: `${registry}/olve-pipelines`,
        version,
        image: `${to.repository}:${server.tag}`,
      });
    }
  },
);
