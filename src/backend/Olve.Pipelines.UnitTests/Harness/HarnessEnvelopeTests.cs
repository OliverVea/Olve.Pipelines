using Olve.Pipelines.Kubernetes.Harness;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessEnvelopeTests
{
    [Test]
    public async Task Succeeded_KeepsRawOutput()
    {
        var parsed = HarnessEnvelope.Parse("""{"output":{"sha":"abc","n":1},"status":"succeeded"}""");

        await Assert.That(parsed).Succeeded();
        var s = (HarnessEnvelope.Succeeded)parsed.Value!;
        await Assert.That(s.OutputOrNull).IsEqualTo("""{"sha":"abc","n":1}""");
    }

    [Test]
    public async Task Succeeded_NullOutput()
    {
        var parsed = (HarnessEnvelope.Succeeded)HarnessEnvelope.Parse("""{"output":null,"status":"succeeded"}""").Value!;

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
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("""{"status":"succeeded"}""")]
    [Arguments("""{"status":"succeeded","output":1,"extra":2}""")]
    [Arguments("""{"status":"succeeded","status":"succeeded","output":1}""")]
    [Arguments("""{"status":"skipped"}""")]
    [Arguments("""{"status":"skipped","reason":5}""")]
    [Arguments("""{"status":"failed","error":{"kind":"boom","message":"x"}}""")]
    [Arguments("""{"status":"failed","error":"x"}""")]
    [Arguments("""{"status":"weird"}""")]
    [Arguments("""{"status":1}""")]
    public async Task Malformed_IsRejected(string json)
    {
        await Assert.That(HarnessEnvelope.Parse(json)).Failed();
    }

    [Test]
    public async Task IndentedOutputNearTheCap_IsAcceptedCompacted()
    {
        // ~35 KB compact, well over 64 KB once indented like canonicalJson.
        var items = string.Join(",\n", Enumerable.Range(0, 6000).Select(i => $"      {i}"));
        var json = "{\n  \"output\": {\n    \"xs\": [\n" + items + "\n    ]\n  },\n  \"status\": \"succeeded\"\n}\n";

        var parsed = HarnessEnvelope.Parse(json);

        await Assert.That(parsed).Succeeded();
        await Assert.That(((HarnessEnvelope.Succeeded)parsed.Value!).OutputJson).StartsWith("""{"xs":[0,1,2""");
    }

    [Test]
    public async Task OversizedOutput_IsRejected()
    {
        var big = new string('x', 70 * 1024);
        await Assert.That(HarnessEnvelope.Parse($$"""{"output":"{{big}}","status":"succeeded"}""", maxBytes: 1024 * 1024)).Failed();
    }
}
