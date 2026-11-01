using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Configs;

public class YamlJsonTests
{
    [Test]
    public async Task CoreSchema_TypesPlainScalars_AndKeepsQuotedOnesStrings()
    {
        var result = YamlJson.Parse("""
            # comment
            a: true
            b: no
            c: 42
            d: 1.5
            e: ~
            f: "true"
            g: [x, 2]
            h:
              nested: value
            """);

        await Assert.That(result).SucceededAndValue(v => v.Satisfies(
            o => o!.ToJsonString(),
            json => json.IsEqualTo("""{"a":true,"b":"no","c":42,"d":1.5,"e":null,"f":"true","g":["x",2],"h":{"nested":"value"}}""")));
    }

    [Test]
    [Arguments("")]
    [Arguments("# only a comment\n")]
    public async Task EmptyDocument_IsAnEmptyMapping(string yaml)
        => await Assert.That(YamlJson.Parse(yaml)).SucceededAndValue(v => v.Satisfies(o => o!.Count, c => c.IsEqualTo(0)));

    [Test]
    public async Task ParserErrors_CarryTheLine()
    {
        await Assert.That(ConfigManifests.Problems(YamlJson.Parse("a: 1\nb: [1, 2\n"))).StartsWith("yaml: line 3: ");
    }

    [Test]
    public async Task NumbersInOtherBases_AndExponents_AreNumbers()
        => await Assert.That(YamlJson.Parse("a: 0x1F\nb: 0o17\nc: 1e3\n")).SucceededAndValue(v => v.Satisfies(
            o => o!.ToJsonString(), json => json.IsEqualTo("""{"a":31,"b":15,"c":1000}""")));

    [Test]
    public async Task ParsedValues_AreDetachedNodes()
    {
        var doc = TestRunDomain.Pick(YamlJson.Parse("a: {b: 1}\n"));
        var copy = new JsonObject { ["x"] = doc["a"]!.DeepClone() };

        await Assert.That(copy.ToJsonString()).IsEqualTo("""{"x":{"b":1}}""");
    }
}
