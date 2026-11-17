// Exact tags: step pods pull IfNotPresent, so a floating tag stays at whatever the node cached first.
export const DOTNET_SDK = 'mcr.microsoft.com/dotnet/sdk:11.0.100-resolute';
export const DOTNET_AOT_SDK = 'mcr.microsoft.com/dotnet/sdk:11.0.100-resolute-aot';
export const CRANE_IMAGE =
  'gcr.io/go-containerregistry/crane/debug:v0.21.9@sha256:ba318977330d2a41fdd4af7536d35567a4ceb204252b7dfcd19b01dad2567793';
export const HELM_IMAGE =
  'alpine/helm:3.16.2@sha256:a19a2968fd672336d39771f6c899781424d725229148656dbc2a1e305003cdec';
