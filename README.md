# Olve.Pipelines

A .NET 10 minimal API service template. Install with `dotnet new` and scaffold a full solution with auth, telemetry, Helm chart, and client generation.

## Usage

```bash
# Install the template
dotnet new install .

# Create a new project
dotnet new olve-api -n "MyCompany.MyService"
```

## Project Structure

```
src/Olve.Pipelines/                          # API application (minimal API)
├── Configuration/                              # Auth, telemetry, JSON, host config
├── Message/                                    # Message service (example feature)
├── Health/                                     # Health check endpoints
├── Dockerfile                                  # Multi-stage build (AOT, chiseled)
└── appsettings.json                            # Default configuration
test/Olve.Pipelines.UnitTests/               # Unit tests (TUnit + Rocks)
test/Olve.Pipelines.IntegrationTests/        # Integration tests (TUnit + WebApplicationFactory)
clients/Olve.Pipelines.Client/               # Generated C# client (Refitter source gen)
clients/olve-pipelines-client-ts/            # Generated TypeScript client (Kiota)
tools/version.cs                                # CalVer versioning script
helm/                                           # Helm chart for Kubernetes
Directory.Build.props                           # Shared build properties (TFM, nullable, etc.)
Directory.Packages.props                        # Central package version management
```

## Endpoints

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/health` | No | Health check, returns 200 |
| GET | `/message` | No | Retrieve stored message |
| POST | `/message?message=<text>` | Yes (JWT) | Store a message |
| GET | `/openapi/v1.json` | No | OpenAPI spec |

## Build & Test

```bash
# Restore and build
dotnet restore
dotnet build

# Unit tests only (default)
dotnet test

# Integration tests only
dotnet test -p:RunIntegrationTests=true -p:RunUnitTests=false

# All tests
dotnet test -p:RunIntegrationTests=true
```

Integration tests use `WebApplicationFactory` with [Testcontainers](https://dotnet.testcontainers.org/) for external dependencies. The `AppFixture` class manages the test server and container lifecycle via TUnit's `ClassDataSource` pattern.

To add a dependency (e.g. PostgreSQL):

1. Add the Testcontainers module to `Directory.Packages.props` and the integration test project:
   ```xml
   <!-- Directory.Packages.props -->
   <PackageVersion Include="Testcontainers.PostgreSql" Version="4.11.0" />

   <!-- Integration test .csproj -->
   <PackageReference Include="Testcontainers.PostgreSql" />
   ```

2. Update `AppFixture` to start the container and wire the connection string:
   ```csharp
   private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().Build();

   public async Task InitializeAsync()
   {
       await _pg.StartAsync();

       _factory = new WebApplicationFactory<Program>()
           .WithWebHostBuilder(builder =>
           {
               // ... existing config ...
               builder.UseSetting("ConnectionStrings:Default", _pg.GetConnectionString());
           });
   }

   public async ValueTask DisposeAsync()
   {
       await _factory.DisposeAsync();
       await _pg.DisposeAsync();
   }
   ```

Test execution is controlled by MSBuild properties:
- `RunUnitTests=false` skips unit tests
- `RunIntegrationTests=true` enables integration tests (disabled by default)

## Running

```bash
# Local
dotnet run --project src/Olve.Pipelines

# Kubernetes
helm install olve-pipelines helm/
```

## Configuration

Sources in priority order (highest wins):

1. CLI args (`--Port 9090`)
2. User secrets (`dotnet user-secrets set "Key" "value"`)
3. Environment variables
4. `appsettings.{Environment}.json`
5. `appsettings.json`

| Key | Default | Description |
|-----|---------|-------------|
| `Host` | `localhost` | Listen address |
| `Port` | `5000` | Listen port |
| `Auth:Authority` | `https://auth.ovea.pro/...` | OIDC authority (Authentik) |
| `Auth:Audience` | `olve-pipelines` | JWT audience |
| `Auth:SigningKey` | _(null)_ | Local HS256 key (bypasses OIDC, for dev) |
| `OpenTelemetry:Endpoint` | `https://otel.ovea.pro` | OTLP endpoint (null = disabled) |

## Client Generation

### C# ([Refitter](https://refitter.github.io/))

The `clients/Olve.Pipelines.Client/` project uses the [Refitter source generator](https://www.nuget.org/packages/Refitter.SourceGenerator) to produce a typed [Refit](https://github.com/reactiveui/refit) interface from `api.json` at build time. Just build the solution — no manual codegen step needed.

### TypeScript ([Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview))

```bash
dotnet tool restore
dotnet kiota generate -l typescript -d api.json -c OlveTemplateApiClient -o clients/olve-pipelines-client-ts/src -n OlveTemplateApi
```

## Versioning

The `tools/version.cs` script computes CalVer versions:

```bash
# Local development
dotnet run tools/version.cs
# version=0.0.0-dev+cb9a99b

# CI (pass run number from GitHub Actions)
dotnet run tools/version.cs -- --ci --run-number 42
# version=2026.3.28.42+cb9a99b

# With runtime identifier for artifact naming
dotnet run tools/version.cs -- --ci --run-number 42 --rid linux-x64
# artifact-name=olve-pipelines-2026.3.28.42+cb9a99b-linux-x64
```

## CI

This template does not include a CI workflow — the actual workflow should live in the service's deployment repo. Below are examples to copy and adapt.

### Example: PR workflow

```yaml
# .github/workflows/pr.yml
name: PR

on:
  pull_request:
    branches: [main]

env:
  DOTNET_VERSION: 10.0.100

jobs:
  build-and-test:
    name: Build and test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-restore --no-build -c Release
      - run: dotnet test --no-restore --no-build -c Release -p:RunIntegrationTests=true -p:RunUnitTests=false
```

### Example: Push to main workflow

```yaml
# .github/workflows/push-main.yml
name: Push to main

on:
  push:
    branches: [main]

env:
  DOTNET_VERSION: 10.0.100

jobs:
  build-and-test:
    name: Build and test
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
      - run: dotnet restore
      - run: dotnet build --no-restore -c Release
      - run: dotnet test --no-restore --no-build -c Release
      - run: dotnet test --no-restore --no-build -c Release -p:RunIntegrationTests=true -p:RunUnitTests=false

  version:
    name: Compute version
    needs: build-and-test
    runs-on: ubuntu-latest
    outputs:
      version: ${{ steps.version.outputs.version }}
      artifact-name: ${{ steps.version.outputs.artifact-name }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
      - name: Compute version
        id: version
        run: |
          dotnet run tools/version.cs -- --ci --run-number ${{ github.run_number }} \
            | tee -a "$GITHUB_OUTPUT"
```

## References

- [Olve.MinimalApi](https://olivervea.github.io/Olve.Utilities/src/Olve.MinimalApi/README.html) — Minimal API extensions for result mapping, validation, and JSON conversion
- [Olve.Results](https://olivervea.github.io/Olve.Utilities/src/Olve.Results/README.html) — Functional result types for non-throwing error handling
- [Olve.Validation](https://olivervea.github.io/Olve.Utilities/src/Olve.Validation/README.html) — Fluent input validation built on Olve.Results
- [Olve.Utilities](https://olivervea.github.io/Olve.Utilities/src/Olve.Utilities/README.html) — Meta-package bundling utility libraries including identifiers, collections, and graph types
- [TUnit](https://tunit.dev/docs/intro) — Test framework (not xUnit/NUnit). Uses `await Assert.That(...)` fluent syntax
- [Rocks](https://raw.githubusercontent.com/JasonBock/Rocks/refs/heads/main/docs/Overview.md) — Source-generated mocking library for AOT-compatible test doubles
- [Refitter](https://refitter.github.io/articles/refitter-file-format.html) — Source generator for typed C# HTTP clients from OpenAPI specs via Refit
- [Kiota](https://learn.microsoft.com/en-us/openapi/kiota/overview) — Microsoft's OpenAPI client generator for TypeScript (and other languages)
