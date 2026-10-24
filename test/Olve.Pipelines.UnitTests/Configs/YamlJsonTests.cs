using System.Text.Json.Nodes;
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
    [Arguments("a: &x 1\nb: *x\n", "anchors")]
    [Arguments("a: 1\na: 2\n", "duplicate key 'a'")]
    [Arguments("- 1\n- 2\n", "must be a mapping")]
    [Arguments("a: 1\n---\nb: 2\n", "only one YAML document")]
    [Arguments("a: [1, 2\n", "line")]
    [Arguments("a: !custom x\n", "tag")]
    public async Task Rejects(string yaml, string message)
    {
        var result = YamlJson.Parse(yaml);

        await Assert.That(result).Failed();
        await Assert.That(ConfigManifests.Problems(result)).Contains(message);
    }

    [Test]
    public async Task Problems_CarryTheLine()
    {
        var result = YamlJson.Parse("a: 1\nb: 2\nb: 3\n");

        await Assert.That(result).Failed();
        await Assert.That(ConfigManifests.Problems(result)).IsEqualTo("yaml: line 3: duplicate key 'b'");
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
