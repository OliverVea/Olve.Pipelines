using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Sources;
using Olve.Pipelines.UnitTests.Manifests;
using Olve.Pipelines.UnitTests.Scheduling;
using Olve.Results;
using Olve.Results.TUnit;
using Olve.Utilities.Ids;

namespace Olve.Pipelines.UnitTests.Triggers;

public class WebhookTriggerTests
{
    private const string Secret = "hook-secret";

    private sealed class Secrets(string? value) : IPipelineSecretReader
    {
        public int Reads { get; private set; }

        public Task<Result<string>> TryGetSecretAsync(Id<Pipeline> pipelineId, string key, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(value is null ? Result.Failure<string>(new ResultProblem("not set")) : Result.Success(value));
        }
    }

    private static readonly JsonObject Hmac = new()
    {
        ["kind"] = "webhook",
        ["name"] = "release",
        ["verify"] = new JsonObject { ["scheme"] = "hmac-sha256", ["secret"] = "HOOK", ["header"] = "X-Hub-Signature-256", ["prefix"] = "sha256=" },
    };

    private static string WithTrigger(JsonObject trigger, bool declareSecret = true) => SchedulerScenario.Mutated(root =>
    {
        root["triggers"]!.AsArray().Add(trigger.DeepClone());
        if (declareSecret)
            root["secrets"]!.AsArray().Add(new JsonObject { ["name"] = "HOOK", ["description"] = "h" });
    });

    private static (WebhookTriggerService Service, SchedulerScenario S) Receiver(string? secretValue = Secret) =>
        Receiver(new Secrets(secretValue));

    private static (WebhookTriggerService Service, SchedulerScenario S) Receiver(Secrets secrets, IConfigLock? configLock = null)
    {
        var s = new SchedulerScenario(WithTrigger(Hmac));
        var services = new ServiceCollection();
        services.AddSingleton(new RunInputService(s.D.ActivationService, s.D.VersionService, s.D.Plans, s.D.Starts, s.D.Continuations));
        services.AddSingleton<IPipelineSecretReader>(secrets);
        if (configLock is not null)
            services.AddSingleton(configLock);
        return (new WebhookTriggerService(services.BuildServiceProvider(), NullLogger<WebhookTriggerService>.Instance), s);
    }

    /// <summary>Counts mappings that reach the config gate; the config is never current, so none starts a run.</summary>
    private sealed class StaleConfig(Task? hold = null) : IConfigLock
    {
        private int _calls;
        public int Calls => _calls;

        public async Task<bool> RunIfConfigCurrentAsync(Id<Pipeline> pipelineId, Func<Task> action, CancellationToken ct, bool force = false)
        {
            Interlocked.Increment(ref _calls);
            if (hold is not null)
                await hold;
            return false;
        }
    }

    private static WebhookDelivery Signed(string deliveryId)
    {
        const string body = """{"release":{}}""";
        return Delivery(body, ("X-Hub-Signature-256", GitHubWebhookSignature.Compute(Secret, Encoding.UTF8.GetBytes(body))), ("X-GitHub-Delivery", deliveryId));
    }

    private static WebhookDelivery Delivery(string body, params (string Name, string Value)[] headers) =>
        new(Encoding.UTF8.GetBytes(body), headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase));

    [Test]
    public async Task Manifest_RoundTripsBothSchemes()
    {
        var bearer = new JsonObject { ["kind"] = "webhook", ["name"] = "deploy", ["verify"] = new JsonObject { ["scheme"] = "bearer", ["secret"] = "HOOK" } };
        var manifest = ManifestParser.Parse(Encoding.UTF8.GetBytes(SchedulerScenario.Mutated(root =>
        {
            root["triggers"]!.AsArray().Add(Hmac.DeepClone());
            root["triggers"]!.AsArray().Add(bearer.DeepClone());
            root["secrets"]!.AsArray().Add(new JsonObject { ["name"] = "HOOK", ["description"] = "h" });
        })));

        await Assert.That(manifest).Succeeded();
        var hooks = TestRunDomain.Pick(manifest).Triggers.OfType<WebhookTrigger>().ToList();
        await Assert.That(hooks[0].Verify).IsEqualTo(new HmacSha256TriggerVerify("HOOK", "X-Hub-Signature-256", "sha256="));
        await Assert.That(hooks[1].Verify).IsEqualTo(new BearerTriggerVerify("HOOK"));
        var written = JsonNode.Parse(ManifestWriter.ToCanonicalJson(TestRunDomain.Pick(manifest)))!["triggers"]!.AsArray();
        await Assert.That(JsonNode.DeepEquals(written.First(t => t!["name"]!.GetValue<string>() == "release"), Hmac)).IsTrue();
    }

    [Test]
    public async Task Validator_RejectsUndeclaredSecret_AndBadName()
    {
        var undeclared = ManifestValidator.Validate(TestRunDomain.Pick(ManifestParser.Parse(Encoding.UTF8.GetBytes(WithTrigger(Hmac, declareSecret: false)))));
        var badName = Hmac.DeepClone().AsObject();
        badName["name"] = "Bad_Name";
        var named = ManifestValidator.Validate(TestRunDomain.Pick(ManifestParser.Parse(Encoding.UTF8.GetBytes(WithTrigger(badName)))));

        await Assert.That(undeclared.Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.WebhookSecretUndeclared);
        await Assert.That(named.Errors.Select(e => e.Code)).Contains(ManifestIssueCodes.TriggerNameInvalid);
    }

    [Test]
    public async Task EventJson_LowerCasesHeaders_HidesCredentials_AndParsesJsonBodies()
    {
        var verify = new HmacSha256TriggerVerify("HOOK", "X-Hub-Signature-256", "sha256=");
        var json = JsonNode.Parse(WebhookTriggerService.EventJson(
            Delivery("""{"a":1}""", ("X-GitHub-Event", "release"), ("Authorization", "Bearer x"), ("Cookie", "c"), ("X-Hub-Signature-256", "sha256=00")), verify))!;
        var text = JsonNode.Parse(WebhookTriggerService.EventJson(Delivery("plain"), verify))!;

        await Assert.That(json["headers"]!.AsObject().Select(kv => kv.Key)).IsEquivalentTo(["x-github-event"]);
        await Assert.That(json["body"]!["a"]!.GetValue<int>()).IsEqualTo(1);
        await Assert.That(text["body"]!.GetValue<string>()).IsEqualTo("plain");
    }

    [Test]
    public async Task Receive_ChecksTrigger_Secret_AndSignature()
    {
        var (service, s) = Receiver();
        const string body = """{"release":{}}""";
        var signed = Delivery(body, ("X-Hub-Signature-256", GitHubWebhookSignature.Compute(Secret, Encoding.UTF8.GetBytes(body))));

        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "nope", signed, CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.NotFound);
        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "poll", signed, CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.NotFound);
        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Delivery(body, ("X-Hub-Signature-256", "sha256=00")), CancellationToken.None))
            .IsEqualTo(WebhookTriggerReceipt.Unauthorized);
        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", signed, CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Accepted);

        var (unset, s2) = Receiver(secretValue: null);
        await Assert.That(await unset.ReceiveAsync(s2.Pipeline.Id, "release", signed, CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Unavailable);
    }

    [Test]
    public async Task Receive_CachesTheSecret_SoUnsignedFloodsStayOffKubernetes()
    {
        var secrets = new Secrets(Secret);
        var (service, s) = Receiver(secrets);
        const string body = """{"release":{}}""";

        for (var i = 0; i < 20; i++)
            await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Delivery(body, ("X-Hub-Signature-256", "sha256=00")), CancellationToken.None))
                .IsEqualTo(WebhookTriggerReceipt.Unauthorized);
        var signed = Delivery(body, ("X-Hub-Signature-256", GitHubWebhookSignature.Compute(Secret, Encoding.UTF8.GetBytes(body))));
        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", signed, CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Accepted);

        await Assert.That(secrets.Reads).IsEqualTo(1);
    }

    // Catches: unauthenticated requests reading the K8s Secret on every hit while the secret is unset.
    [Test]
    public async Task Receive_UnsetSecret_IsReadOnceNotPerRequest()
    {
        var secrets = new Secrets(null);
        var (service, s) = Receiver(secrets);

        for (var i = 0; i < 20; i++)
            await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Signed($"d{i}"), CancellationToken.None))
                .IsEqualTo(WebhookTriggerReceipt.Unavailable);

        await Assert.That(secrets.Reads).IsEqualTo(1);
    }

    [Test]
    public async Task Redelivery_OfADeliveryThatStartedNothing_IsMappedAgain()
    {
        var gate = new StaleConfig();
        var (service, s) = Receiver(new Secrets(Secret), gate);

        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Signed("d1"), CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Accepted);
        await service.WhenMappedAsync();
        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Signed("d1"), CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Accepted);
        await service.WhenMappedAsync();

        await Assert.That(gate.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Redelivery_WhileTheFirstIsBeingMapped_IsIgnored()
    {
        var release = new TaskCompletionSource();
        var gate = new StaleConfig(release.Task);
        var (service, s) = Receiver(new Secrets(Secret), gate);

        await service.ReceiveAsync(s.Pipeline.Id, "release", Signed("d1"), CancellationToken.None);
        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Signed("d1"), CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Accepted);
        release.SetResult();
        await service.WhenMappedAsync();

        await Assert.That(gate.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Redelivery_OfADeliveryTurnedAwayBusy_IsAdmitted()
    {
        var release = new TaskCompletionSource();
        var gate = new StaleConfig(release.Task);
        var (service, s) = Receiver(new Secrets(Secret), gate);

        for (var i = 0; i < WebhookTriggerService.MaxInFlight; i++)
            await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Signed($"fill-{i}"), CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Accepted);
        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Signed("late"), CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Busy);
        release.SetResult();
        await service.WhenMappedAsync();

        await Assert.That(await service.ReceiveAsync(s.Pipeline.Id, "release", Signed("late"), CancellationToken.None)).IsEqualTo(WebhookTriggerReceipt.Accepted);
        await service.WhenMappedAsync();
        await Assert.That(gate.Calls).IsEqualTo(WebhookTriggerService.MaxInFlight + 1);
    }
}
