using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared.Problems;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Kubernetes;

/// <summary>
/// The pipeline Secret reader over a real <see cref="KubernetesClient"/> against a loopback API server. Webhook
/// verification and poll headers depend on telling "not set" (404 / no key) from "Kubernetes is down".
/// </summary>
public class PipelineSecretReaderTests
{
    private static readonly Id<Pipeline> P = Id.New<Pipeline>();

    private static string SecretJson(params (string Key, string Value)[] data)
    {
        var encoded = new JsonObject();
        foreach (var (key, value) in data)
            encoded[key] = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        return new JsonObject { ["metadata"] = new JsonObject { ["name"] = PipelineSecrets.Name(P) }, ["data"] = encoded }.ToJsonString();
    }

    private static KubernetesPipelineSecretReader Reader(LoopbackHttpServer api)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new KubernetesClient(new KubernetesCredentials("token", $"http://127.0.0.1:{api.Port}", "", "runners")));
        services.AddSingleton(new KubernetesOptions("runners"));
        return new KubernetesPipelineSecretReader(services.BuildServiceProvider());
    }

    private static bool Is<TProblem>(Result<string> result) =>
        result.TryPickProblems(out var problems) && problems.OfType<TProblem>().Any();

    [Test]
    public async Task ReadsTheKey_FromThePipelinesSecret()
    {
        await using var api = LoopbackHttpServer.Start(LoopbackHttpServer.Response(200, SecretJson(("HOOK", "s3cret"), ("OTHER", "x"))));

        var value = await Reader(api).TryGetSecretAsync(P, "HOOK");

        await Assert.That(value).SucceededAndValue(v => v.IsEqualTo("s3cret"));
        await Assert.That(api.Requests.Single()).StartsWith($"GET /api/v1/namespaces/runners/secrets/{PipelineSecrets.Name(P)} ");
    }

    // Catches: an unset secret reported as an outage (or the reverse), which changes what a webhook sender is told.
    [Test]
    public async Task NoSecret_NoKey_OrAnEmptyValue_IsNotFound()
    {
        await using var noSecret = LoopbackHttpServer.Start(LoopbackHttpServer.Response(404, "{}"));
        await using var noKey = LoopbackHttpServer.Start(LoopbackHttpServer.Response(200, SecretJson(("OTHER", "x"))));
        await using var empty = LoopbackHttpServer.Start(LoopbackHttpServer.Response(200, SecretJson(("HOOK", ""))));

        await Assert.That(Is<NotFoundProblem>(await Reader(noSecret).TryGetSecretAsync(P, "HOOK"))).IsTrue();
        await Assert.That(Is<NotFoundProblem>(await Reader(noKey).TryGetSecretAsync(P, "HOOK"))).IsTrue();
        await Assert.That(Is<NotFoundProblem>(await Reader(empty).TryGetSecretAsync(P, "HOOK"))).IsTrue();
    }

    [Test]
    public async Task ApiFailure_OrNoKubernetes_IsUnavailable()
    {
        await using var failing = LoopbackHttpServer.Start(LoopbackHttpServer.Response(500, "{}"));
        var withoutKubernetes = new KubernetesPipelineSecretReader(new ServiceCollection().BuildServiceProvider());

        await Assert.That(Is<UnavailableProblem>(await Reader(failing).TryGetSecretAsync(P, "HOOK"))).IsTrue();
        await Assert.That(Is<UnavailableProblem>(await withoutKubernetes.TryGetSecretAsync(P, "HOOK"))).IsTrue();
    }

    // Catches: a cached ServiceAccount token, which breaks every K8s call once the projected token rotates (~hourly).
    [Test]
    public async Task WithATokenProvider_EveryRequestCarriesTheCurrentToken()
    {
        await using var api = LoopbackHttpServer.Start(LoopbackHttpServer.Response(200, SecretJson(("HOOK", "v"))));
        var tokens = new Queue<string>(["first", "rotated"]);
        using var client = new KubernetesClient(new KubernetesCredentials("unused", $"http://127.0.0.1:{api.Port}", "", "runners"),
            tokenProvider: _ => ValueTask.FromResult(tokens.Dequeue()));

        await client.GetSecretAsync("runners", "s");
        await client.GetSecretAsync("runners", "s");

        var auth = api.Requests.Select(r => r.Split("\r\n").Single(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))).ToList();
        await Assert.That(auth).IsEquivalentTo(["Authorization: Bearer first", "Authorization: Bearer rotated"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }
}
