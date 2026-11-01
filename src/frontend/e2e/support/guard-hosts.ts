import { request } from 'node:https';
import { HostApi, newName, repoOf } from './api.ts';
import { loadTargets, type TestTarget } from './hosts.ts';

const MARKER_SECRET = 'olve-real-system-marker';

function readClusterSecret(
  { kubernetes: k }: TestTarget,
  name: string,
): Promise<Record<string, string> | null> {
  const url = `${k.server}/api/v1/namespaces/${k.namespace}/secrets/${name}`;
  return new Promise((resolve, reject) => {
    const req = request(
      url,
      { ca: k.caCertificate, headers: { Authorization: `Bearer ${k.token}` } },
      (res) => {
        let body = '';
        res.on('data', (chunk) => (body += chunk));
        res.on('end', () => {
          if (res.statusCode === 404) resolve(null);
          else if (res.statusCode === 200) resolve(JSON.parse(body).data ?? {});
          else reject(new Error(`${url} answered ${res.statusCode}: ${body}`));
        });
      },
    );
    req.on('error', reject);
    req.end();
  });
}

/** The real-system tests' guard: nothing touches a target that isn't a marked test cluster. */
async function guard(target: TestTarget) {
  const { apiUrl, clusterId, kubernetes } = target;
  const refuse = (why: string) => new Error(`Refusing ${apiUrl}: ${why}`);

  if (new URL(apiUrl).hostname.endsWith('.ovea.pro'))
    throw refuse('it is a real instance, not a test host.');
  if (!clusterId?.trim()) throw refuse('its descriptor names no test cluster id.');
  if (new URL(kubernetes.server).port === '6443')
    throw refuse('port 6443 is where a real cluster listens.');

  const marker = await readClusterSecret(target, MARKER_SECRET);
  if (!marker) throw refuse(`its cluster has no ${MARKER_SECRET} Secret.`);
  const seen = Buffer.from(marker['cluster-id'] ?? '', 'base64').toString();
  if (seen !== clusterId)
    throw refuse(`its cluster is '${seen}', not '${clusterId}' as the descriptor says.`);

  const api = new HostApi(apiUrl);
  const name = newName('guard');
  const pipeline = await api.bind(repoOf(name));
  await api.setSecret({ id: pipeline, name, repo: repoOf(name) }, 'GUARD', 'guard');
  const written = await readClusterSecret(
    target,
    `olve-pipeline-${pipeline.replaceAll('-', '')}`,
  );
  if (!written) throw refuse('it wrote its secret outside the test cluster.');

  const index = await fetch(apiUrl);
  if (!index.ok)
    throw new Error(
      `No web UI on ${apiUrl}: / answered ${index.status}. Build it with tools/real-system/host.sh ui.`,
    );
}

export default async function guardHosts() {
  await Promise.all(loadTargets().map(guard));
}
