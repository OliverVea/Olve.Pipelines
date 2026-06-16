FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update && apt-get install -y clang zlib1g-dev
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Olve.Pipelines/Olve.Pipelines.csproj src/Olve.Pipelines/
COPY src/Olve.Pipelines.Cli/Olve.Pipelines.Cli.csproj src/Olve.Pipelines.Cli/
COPY test/Olve.Pipelines.UnitTests/Olve.Pipelines.UnitTests.csproj test/Olve.Pipelines.UnitTests/
RUN dotnet restore src/Olve.Pipelines -r linux-x64 && dotnet restore test/Olve.Pipelines.UnitTests

COPY src/Olve.Pipelines/ src/Olve.Pipelines/
COPY src/Olve.Pipelines.Cli/ src/Olve.Pipelines.Cli/
COPY test/Olve.Pipelines.UnitTests/ test/Olve.Pipelines.UnitTests/
RUN dotnet run --project test/Olve.Pipelines.UnitTests -c Release --no-restore
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
