# The contract: src/spec/main.tsp → artifacts/ (the OpenAPI document, the server's generated C# and the TypeScript client).
FROM oven/bun:1.4.3-slim AS spec
WORKDIR /src
COPY src/spec/package.json src/spec/bun.lock src/spec/
RUN cd src/spec && bun install --frozen-lockfile
COPY src/spec/ src/spec/
RUN cd src/spec && bun run build

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update && apt-get install -y clang zlib1g-dev
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/backend/Olve.Pipelines/Olve.Pipelines.csproj src/backend/Olve.Pipelines/
COPY src/backend/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj src/backend/Olve.Pipelines.Cli/
RUN dotnet restore src/backend/Olve.Pipelines -r linux-x64

COPY src/backend/Olve.Pipelines/ src/backend/Olve.Pipelines/
COPY src/backend/Olve.Pipelines.Cli/ src/backend/Olve.Pipelines.Cli/
COPY --from=spec /src/artifacts/generated/backend/ artifacts/generated/backend/
# The setup guide is served at /docs; the csproj globs docs/setup/*.md into wwwroot on publish.
COPY docs/setup/ docs/setup/
# The pl install scripts, served at /install.sh and /install.ps1 (the csproj copies them next to the binary).
COPY install.sh install.ps1 ./
# Tests are NOT run in the image build — the pipeline's `test` step runs them beside the build.
RUN dotnet publish src/backend/Olve.Pipelines -c Release -r linux-x64 -p:DebugType=none -p:SkipSpecGen=true -o /app

FROM node:22-slim AS frontend
WORKDIR /build
COPY src/clients/olve-pipelines-client-ts/ ./src/clients/olve-pipelines-client-ts/
RUN cd src/clients/olve-pipelines-client-ts && npm install
# The build's `prebuild` hook (scripts/build-docs-index.mjs) reads ../../docs/setup to emit
# the client-side search index, so the docs must be present in this stage too.
COPY docs/setup/ ./docs/setup/
# Manifests first so the npm install layer is cached until dependencies change.
COPY src/frontend/package.json src/frontend/package-lock.json ./src/frontend/
RUN cd src/frontend && npm install
# The contract's TypeScript client (`@olve/api`), resolved from src/frontend as ../../artifacts/clients/ts.
COPY --from=spec /src/artifacts/clients/ts artifacts/clients/ts
COPY src/frontend/ ./src/frontend/
RUN cd src/frontend && npm run build

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
EXPOSE 5000
COPY --from=build /app .
COPY --from=frontend /build/src/frontend/dist ./wwwroot/

ENTRYPOINT ["./Olve.Pipelines"]
