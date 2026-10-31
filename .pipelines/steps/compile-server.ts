import { cpSync } from 'node:fs';
import { join } from 'node:path';
import { implement, step, Source } from '@olivervea/pipelines';
import { BUILD } from '../config';
import * as M from '../models';
import { DOTNET_AOT_SDK } from '../images';
import { layerTar } from '../layers';

const SERVER = 'src/backend/Olve.Pipelines';

export const compileServer = implement(
  step({
    name: 'compile-server',
    description:
      'Publishes the server (API and docs) as Native AOT or JIT, per build.runtime, as an image layer.',
    image: DOTNET_AOT_SDK,
    input: { source: Source, spec: M.SpecBuild },
    output: M.ServerBuild,
    config: [BUILD],
    timeoutSeconds: 1800,
  }),
  async ({ source, spec }, ctx) => {
    const runtime = ctx.config.build.runtime;
    const tree = await ctx.files.fetch(source.tree);
    cpSync(await ctx.files.fetch(spec.artifacts), join(tree, 'artifacts'), {
      recursive: true,
    });
    const root = join(ctx.tmpDir, 'server');
    const env = { MSBUILDDISABLENODEREUSE: '1' };
    // JIT keeps the source generators PublishAot turns on (JSON, endpoints, config binding), so both run the same code paths.
    const flavour =
      runtime === 'aot'
        ? ['-p:DebugType=none']
        : [
            '-p:PublishAot=false',
            '--self-contained',
            'false',
            '-p:JsonSerializerIsReflectionEnabledByDefault=false',
            '-p:EnableRequestDelegateGenerator=true',
            '-p:EnableConfigurationBindingGenerator=true',
          ];
    await ctx.exec(
      [
        'dotnet',
        'publish',
        SERVER,
        '-c',
        'Release',
        '-r',
        'linux-x64',
        '-m:1',
        '-p:UseSharedCompilation=false',
        '-p:SkipSpecGen=true',
        ...flavour,
        '-o',
        join(root, 'app'),
      ],
      { cwd: tree, env },
    );

    const layer = await ctx.output.file(await layerTar(ctx, root, 'server.tar.gz'), {
      name: 'server.tar.gz',
    });
    ctx.log('published the server', { runtime });
    return { runtime, layer };
  },
);
