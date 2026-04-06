FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
RUN apt-get update && apt-get install -y clang zlib1g-dev
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Olve.Pipelines/Olve.Pipelines.csproj src/Olve.Pipelines/
RUN dotnet restore src/Olve.Pipelines -r linux-x64

COPY src/Olve.Pipelines/ src/Olve.Pipelines/
RUN dotnet publish src/Olve.Pipelines -c Release -r linux-x64 -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled
WORKDIR /app
EXPOSE 5000
COPY --from=build /app .

ENTRYPOINT ["./Olve.Pipelines"]
