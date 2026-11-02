import {
  type ConfigRef,
  implement,
  resolveConfigRefs,
  Source,
  sshSession,
  step,
} from '@olivervea/pipelines';
import { SSH_PRIVATE_KEY } from '../config';

export const realSystemTest = (host: ConfigRef) =>
  implement(
    step({
      name: 'real-system-test',
      description:
        'Runs the real-system and browser tests on a throwaway host on the beta machine: this commit must pass them to reach prod.',
      image: 'alpine:3.22',
      input: { source: Source },
      secrets: [SSH_PRIVATE_KEY],
      settings: { host },
      timeoutSeconds: 1800,
    }),
    async ({ source }, ctx) => {
      const { host: address } = resolveConfigRefs({ host }, ctx.config ?? {}) as {
        host: string;
      };
      const tree = await ctx.files.fetch(source.tree);
      const ssh = await sshSession(ctx, {
        host: address,
        key: ctx.secrets.SSH_PRIVATE_KEY,
      });
      const remote = `.cache/olve-gate/${source.sha}`;
      await ssh.run(`rm -rf ${remote} && mkdir -p ${remote}`);
      await ctx.sh(`tar -C '${tree}' -cf - . | ${ssh.shell(`tar -xf - -C ${remote}`)}`);
      await ssh.run(`bash ${remote}/tools/real-system/gate.sh`, { tty: true });
      ctx.log('real-system and browser tests passed', { sha: source.sha });
    },
  );
