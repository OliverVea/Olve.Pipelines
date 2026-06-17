FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update && apt-get install -y clang zlib1g-dev
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Olve.Pipelines/Olve.Pipelines.csproj src/Olve.Pipelines/
COPY src/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj src/Olve.Pipelines.Cli/
RUN dotnet restore src/Olve.Pipelines -r linux-x64

COPY src/Olve.Pipelines/ src/Olve.Pipelines/
COPY src/Olve.Pipelines.Cli/ src/Olve.Pipelines.Cli/
# The setup guide is served at /docs; the csproj globs docs/setup/*.md into wwwroot on publish.
COPY docs/setup/ docs/setup/
# Tests are NOT run in the image build — they run in the `code-test` production step in
# .pipelines/config.yaml, in parallel with this build rather than serialized inside it.
RUN dotnet publish src/Olve.Pipelines -c Release -r linux-x64 -o /app

FROM node:22-slim AS frontend
WORKDIR /build
COPY clients/olve-pipelines-client-ts/ ./clients/olve-pipelines-client-ts/
RUN cd clients/olve-pipelines-client-ts && npm install
COPY frontend/ ./frontend/
RUN cd frontend && npm install && npm run build

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
EXPOSE 5000
COPY --from=build /app .
COPY --from=frontend /build/frontend/dist ./wwwroot/

ENTRYPOINT ["./Olve.Pipelines"]
