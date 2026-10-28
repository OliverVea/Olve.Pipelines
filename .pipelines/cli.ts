export const CLI = 'src/backend/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj';

export const cliPublishArgs = (version: string) => [
  '-c',
  'Release',
  '-m:1',
  '-p:UseSharedCompilation=false',
  `-p:InformationalVersion=${version}`,
  '-p:IncludeSourceRevisionInInformationalVersion=false',
];
