using Olve.Pipelines.Kubernetes.Harness;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Configs;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessEnvelopeTests
{
    [Test]
    public async Task Succeeded_KeepsRawOutput()
    {
        var parsed = HarnessEnvelope.Parse("""{"output":{"sha":"abc","n":1},"status":"succeeded"}""");

        await Assert.That(parsed).Succeeded();
        var s = (HarnessEnvelope.Succeeded)TestRunDomain.Pick(parsed);
        await Assert.That(s.OutputOrNull).IsEqualTo("""{"sha":"abc","n":1}""");
    }

    [Test]
    public async Task Succeeded_NullOutput()
    {
        var parsed = (HarnessEnvelope.Succeeded)TestRunDomain.Pick(HarnessEnvelope.Parse("""{"output":null,"status":"succeeded"}"""));

        await Assert.That(parsed.OutputOrNull).IsNull();
    }

    [Test]
    public async Task SkippedAndFailed()
    {
        var skipped = HarnessEnvelope.Parse("""{"reason":"nothing to do","status":"skipped"}""").Value;
        var failed = HarnessEnvelope.Parse("""{"error":{"kind":"input","message":"bad"},"status":"failed"}""").Value;

        await Assert.That(skipped).IsEqualTo(new HarnessEnvelope.Skipped("nothing to do"));
        await Assert.That(failed).IsEqualTo(new HarnessEnvelope.Failed("input", "bad"));
    }

    [Test]
    public async Task NotJson_IsRejected()
    {
        await Assert.That(ConfigManifests.Problems(HarnessEnvelope.Parse("not json"))).StartsWith("Invalid harness envelope: envelope is not valid JSON: ");
    }

    [Test]
    public async Task IndentedOutputNearTheCap_IsAcceptedCompacted()
    {
        // ~35 KB compact, well over 64 KB once indented like canonicalJson.
        var items = string.Join(",\n", Enumerable.Range(0, 6000).Select(i => $"      {i}"));
        var json = "{\n  \"output\": {\n    \"xs\": [\n" + items + "\n    ]\n  },\n  \"status\": \"succeeded\"\n}\n";

        var parsed = HarnessEnvelope.Parse(json);

        await Assert.That(parsed).Succeeded();
        await Assert.That(((HarnessEnvelope.Succeeded)TestRunDomain.Pick(parsed)).OutputJson).StartsWith("""{"xs":[0,1,2""");
    }


    [Test]
    [Arguments("""{"status":"succeeded","output":null,"links":[{"url":"https://app.example.com","label":"App"},{"url":"http://beta.example.com"}]}""")]
    [Arguments("""{"status":"skipped","reason":"r","links":[{"url":"https://app.example.com","label":"App"},{"url":"http://beta.example.com"}]}""")]
    [Arguments("""{"status":"failed","error":{"kind":"handler","message":"m"},"links":[{"url":"https://app.example.com","label":"App"},{"url":"http://beta.example.com"}]}""")]
    public async Task Links_AreKeptOnEveryStatus(string json)
    {
        var parsed = TestRunDomain.Pick(HarnessEnvelope.Parse(json));

        await Assert.That(parsed.Links).IsEquivalentTo(new[] { new AttemptLink("https://app.example.com", "App"), new AttemptLink("http://beta.example.com") });
    }

    [Test]
    [Arguments("""{"status":"succeeded","output":null}""")]
    [Arguments("""{"status":"succeeded","output":null,"links":[]}""")]
    public async Task NoLinks_IsNull(string json)
    {
        await Assert.That(TestRunDomain.Pick(HarnessEnvelope.Parse(json)).Links).IsNull();
    }
}
