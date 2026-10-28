import { fileRef, model, z } from '@olivervea/pipelines';

export const DeployRequest = model(
  'DeployRequest',
  z.object({
    repo: z.string().default('OliverVea/Olve.Pipelines'),
    sha: z.string().regex(/^[0-9a-f]{40}$/),
    deployTo: z.enum(['beta', 'prod']).default('prod'),
  }),
);

/** The contract compiled: `artifacts/` with the OpenAPI document and the server's generated C#. */
export const SpecBuild = model(
  'SpecBuild',
  z.object({
    artifacts: fileRef(),
  }),
);

/** The published server as an image layer (`app/…`), and how it was compiled. */
export const ServerBuild = model(
  'ServerBuild',
  z.object({
    runtime: z.enum(['aot', 'jit']),
    layer: fileRef(),
  }),
);

/** The built frontend as an image layer (`app/wwwroot/…`). */
export const FrontendBuild = model(
  'FrontendBuild',
  z.object({
    layer: fileRef(),
  }),
);

export const CliBuild = model(
  'CliBuild',
  z.object({
    version: z.string(),
    binary: fileRef(),
  }),
);

export const CliRelease = model(
  'CliRelease',
  z.object({
    version: z.string(),
    linuxX64: fileRef(),
    winX64: fileRef(),
  }),
);

export const SdkPackage = model(
  'SdkPackage',
  z.object({
    name: z.string(),
    version: z.string(),
    tarball: fileRef(),
  }),
);

export const SdkRelease = model(
  'SdkRelease',
  z.object({
    name: z.string(),
    version: z.string(),
  }),
);
