import {
  type ConfigRef,
  type HostKey,
  implement,
  resolveConfigRefs,
  Source,
  sshSession,
  step,
} from '@olivervea/pipelines';
import { SSH_PRIVATE_KEY } from '../config';

export const realSystemTest = (host: ConfigRef, hostKey: ConfigRef) =>
  implement(
    step({
      name: 'real-system-test',
      description:
        'Runs the real-system and browser tests on a throwaway host on the beta machine: this commit must pass them to reach prod.',
      image: 'alpine:3.22',
      input: { source: Source },
      secrets: [SSH_PRIVATE_KEY],
      settings: { host, hostKey },
      timeoutSeconds: 1800,
    }),
    async ({ source }, ctx) => {
      const target = resolveConfigRefs({ host, hostKey }, ctx.config ?? {}) as {
        host: string;
        hostKey: HostKey;
      };
      const tree = await ctx.files.fetch(source.tree);
      const ssh = await sshSession(ctx, {
        host: target.host,
        key: ctx.secrets.SSH_PRIVATE_KEY,
        hostKey: target.hostKey,
      });
      const remote = `.cache/olve-gate/${source.sha}`;
      await ssh.run(`rm -rf ${remote} && mkdir -p ${remote}`);
      await ctx.sh(`tar -C '${tree}' -cf - . | ${ssh.shell(`tar -xf - -C ${remote}`)}`);
      await ssh.run(`bash ${remote}/tools/real-system/gate.sh`, { tty: true });
      ctx.log('real-system and browser tests passed', { sha: source.sha });
    },
  );
