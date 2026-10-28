#!/bin/sh
# Builds pl from the checkout for /download (pl-linux-x64 native AOT, pl-win-x64.exe self-contained),
# the way the publish-cli step does. Works on a copy so the checkout's bin/obj stay untouched.
set -eu
rm -rf /tmp/src /tmp/out
mkdir -p /tmp/src
cd /repo
tar --exclude='*/bin' --exclude='*/obj' -cf - Directory.Build.props Directory.Packages.props global.json \
  src/backend/Olve.Pipelines.Cli src/backend/Olve.Pipelines/Kubernetes/Harness/HarnessImage.cs | tar -xf - -C /tmp/src
cd /tmp/src
common="-c Release -p:InformationalVersion=0.0.0-local -p:IncludeSourceRevisionInInformationalVersion=false"
dotnet publish src/backend/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj -r linux-x64 $common -o /tmp/out/linux
dotnet publish src/backend/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj -r win-x64 $common \
  -p:PublishAot=false -p:PublishSingleFile=true -p:SelfContained=true -o /tmp/out/win
/tmp/out/linux/pl --version
cp /tmp/out/linux/pl /dist/pl-linux-x64
cp /tmp/out/win/pl.exe /dist/pl-win-x64.exe
echo "pl built into /dist"
