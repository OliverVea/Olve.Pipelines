using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Olve.Pipelines.Client;
using Refit;
using Testcontainers.Minio;
using TUnit.Core.Interfaces;

namespace Olve.Pipelines.IntegrationTests;

public class AppFixture : IAsyncInitializer, IAsyncDisposable
{
    private const string SigningKey = "integration-test-signing-key-that-is-long-enough";
    private const string Issuer = "integration-test";
    private const string Audience = "integration-test";
    private const int ContainerPort = 5000;
    private const int MinioPort = 9000;

    private INetwork? _network;
    private MinioContainer? _minio;
    private IContainer _container = null!;
    private string _baseUrl = null!;

    private static bool UseBetaS3 =>
        Environment.GetEnvironmentVariable("STORAGE__AUTHURL") is not null
        || Environment.GetEnvironmentVariable("STORAGE__ACCESSKEY") is not null;

    public static bool UseBetaK8s =>
        Environment.GetEnvironmentVariable("KUBERNETES__OPENBAOURL") is not null;

    public async Task InitializeAsync()
    {
        var repoRoot = FindRepoRoot();
        IImage image;

        var prebuiltTag = Environment.GetEnvironmentVariable("INTEGRATION_TEST_IMAGE");
        if (prebuiltTag is not null)
        {
            image = new DockerImage(prebuiltTag);
        }
        else
        {
            var futureImage = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(repoRoot)
                .WithDockerfile("Dockerfile")
                .Build();

            await futureImage.CreateAsync();
            image = futureImage;
        }

        var containerBuilder = new ContainerBuilder(image)
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithEnvironment("Auth__SigningKey", SigningKey)
            .WithEnvironment("Auth__Authority", Issuer)
            .WithEnvironment("Auth__Audience", Audience)
            .WithEnvironment("Host", "0.0.0.0")
            // Drive the GitOps reconcile/deploy poll fast so tests that bind to a repo and wait for
            // shape to materialize don't sit on the 5-minute production default. Kept modest (not
            // sub-second) because the branch-head check is one unauthenticated GitHub call per
            // binding per cycle — tests delete their pipelines promptly to stop the polling.
            .WithEnvironment("Reconcile__PollIntervalSeconds", "5")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                r => r.ForPort(ContainerPort).ForPath("/api/health")));

        if (UseBetaS3)
        {
            // Pass through S3 config from environment (OIDC/STS or static)
            containerBuilder = PassthroughEnv(containerBuilder,
                "STORAGE__ENDPOINT", "STORAGE__BUCKET",
                "STORAGE__ACCESSKEY", "STORAGE__SECRETKEY",
                "STORAGE__AUTHURL", "STORAGE__CLIENTID", "STORAGE__CLIENTSECRET",
                "STORAGE__ROLEARN", "STORAGE__SCOPE");
        }

        if (UseBetaK8s)
        {
            // Pass through K8s config (OpenBao + optional overrides)
            containerBuilder = PassthroughEnv(containerBuilder,
                "KUBERNETES__OPENBAOURL", "KUBERNETES__NAMESPACE",
                "KUBERNETES__S3HELPERIMAGE", "KUBERNETES__DEFAULTIMAGE",
                "STORAGE__SKIPCERTVALIDATION");
        }
        else
        {
            // Spin up local MinIO testcontainer
            _network = new NetworkBuilder().Build();

            _minio = new MinioBuilder("minio/minio:latest")
                .WithNetwork(_network)
                .WithNetworkAliases("minio")
                .Build();

            await _minio.StartAsync();

            containerBuilder = containerBuilder
                .WithNetwork(_network)
                .WithEnvironment("Storage__Endpoint", $"http://minio:{MinioPort}")
                .WithEnvironment("Storage__AccessKey", _minio.GetAccessKey())
                .WithEnvironment("Storage__SecretKey", _minio.GetSecretKey())
                .WithEnvironment("Storage__Bucket", "olve-pipelines-test");
        }

        _container = containerBuilder.Build();
        await _container.StartAsync();

        var hostPort = _container.GetMappedPublicPort(ContainerPort);
        _baseUrl = $"http://localhost:{hostPort}";
    }

    public IOlvePipelinesv1 CreateApiClient()
    {
        var client = CreateAuthenticatedHttpClient();
        return RestService.For<IOlvePipelinesv1>(client);
    }

    public HttpClient CreateAuthenticatedHttpClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GenerateJwt());
        return client;
    }

    public HttpClient CreateUnauthenticatedHttpClient() =>
        new() { BaseAddress = new Uri(_baseUrl) };

    public string? GetMinioConnectionString() =>
        _minio is not null ? _minio.GetConnectionString() : null;

    public string? GetMinioAccessKey() =>
        _minio is not null ? _minio.GetAccessKey() : null;

    public string? GetMinioSecretKey() =>
        _minio is not null ? _minio.GetSecretKey() : null;

    public string MinioBucket => "olve-pipelines-test";

    public async Task RestartAsync()
    {
        await _container.StopAsync();
        await _container.StartAsync();
        var hostPort = _container.GetMappedPublicPort(ContainerPort);
        _baseUrl = $"http://localhost:{hostPort}";
    }

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        if (_minio is not null) await _minio.DisposeAsync();
        if (_network is not null) await _network.DisposeAsync();
    }

    private static ContainerBuilder PassthroughEnv(ContainerBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null)
            {
                // Convert STORAGE__ENDPOINT to Storage__Endpoint format
                var configKey = string.Join("__", key.Split("__")
                    .Select(part => char.ToUpper(part[0]) + part[1..].ToLower()));
                builder = builder.WithEnvironment(configKey, value);
            }
        }
        return builder;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repository root");
    }

    private static string GenerateJwt()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = credentials,
            Subject = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-user")]),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
