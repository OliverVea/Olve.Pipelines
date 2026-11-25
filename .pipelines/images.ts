// Exact tags: step pods pull IfNotPresent, so a floating tag stays at whatever the node cached first.
export const DOTNET_SDK = 'mcr.microsoft.com/dotnet/sdk:11.0.100-resolute';
export const DOTNET_AOT_SDK = 'mcr.microsoft.com/dotnet/sdk:11.0.100-resolute-aot';
export const CRANE_IMAGE =
  'gcr.io/go-containerregistry/crane/debug:v0.21.9@sha256:ba318977330d2a41fdd4af7536d35567a4ceb204252b7dfcd19b01dad2567793';
export const HELM_IMAGE =
  'alpine/helm:4.3.2@sha256:a3b9d617c0c30a6c488cebb429c4ba83995a26dc2184599af5fd408a361b2e67';
