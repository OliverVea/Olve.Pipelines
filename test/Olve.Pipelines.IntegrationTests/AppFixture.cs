using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
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

    private readonly INetwork _network = new NetworkBuilder().Build();

    private MinioContainer _minio = null!;
    private IContainer _container = null!;
    private string _baseUrl = null!;

    public async Task InitializeAsync()
    {
        _minio = new MinioBuilder("minio/minio:latest")
            .WithNetwork(_network)
            .WithNetworkAliases("minio")
            .Build();

        await _minio.StartAsync();

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(CommonDirectoryPath.GetSolutionDirectory(), string.Empty)
            .WithDockerfile("src/Olve.Pipelines/Dockerfile")
            .Build();

        await image.CreateAsync();

        _container = new ContainerBuilder(image)
            .WithNetwork(_network)
            .WithPortBinding(ContainerPort, assignRandomHostPort: true)
            .WithEnvironment("Auth__SigningKey", SigningKey)
            .WithEnvironment("Auth__Authority", Issuer)
            .WithEnvironment("Auth__Audience", Audience)
            .WithEnvironment("Host", "0.0.0.0")
            .WithEnvironment("Storage__Endpoint", $"http://minio:{MinioPort}")
            .WithEnvironment("Storage__AccessKey", _minio.GetAccessKey())
            .WithEnvironment("Storage__SecretKey", _minio.GetSecretKey())
            .WithEnvironment("Storage__Bucket", "olve-pipelines-test")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(ContainerPort).ForPath("/api/health")))
            .Build();

        await _container.StartAsync();

        var hostPort = _container.GetMappedPublicPort(ContainerPort);
        _baseUrl = $"http://localhost:{hostPort}";
    }

    public IOlvePipelinesv1 CreateApiClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GenerateJwt());
        return RestService.For<IOlvePipelinesv1>(client);
    }

    public HttpClient CreateUnauthenticatedHttpClient() =>
        new() { BaseAddress = new Uri(_baseUrl) };

    public async ValueTask DisposeAsync()
    {
        await _container.DisposeAsync();
        await _minio.DisposeAsync();
        await _network.DisposeAsync();
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
