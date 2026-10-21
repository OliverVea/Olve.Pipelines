using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using static Olve.Pipelines.UnitTests.Manifests.ManifestFixture;

namespace Olve.Pipelines.UnitTests.Manifests;

public class CanonicalJsonTests
{
    // Expected outputs were produced by the SDK itself: canonicalJson(JSON.parse(input)) from
    // sdk/src/util/canonical.ts under Bun 1.4.3.
    [Test]
    [Arguments(
        """{"b":1,"10":2,"9":3,"a":{"z":[],"y":{}},"01":4,"4294967295":5,"4294967294":6}""",
        "{\n  \"9\": 3,\n  \"10\": 2,\n  \"4294967294\": 6,\n  \"01\": 4,\n  \"4294967295\": 5,\n  \"a\": {\n    \"y\": {},\n    \"z\": []\n  },\n  \"b\": 1\n}\n")]
    [Arguments(
        "[0.1, 1e21, 1e-7, 1152921504606846976, -0, 123456789012345680000, 1.5e-6, 0.000001, 100, 1e20, 12345.678, -2.5e30, 5e-324, 1.7976931348623157e308]",
        "[\n  0.1,\n  1e+21,\n  1e-7,\n  1152921504606847000,\n  0,\n  123456789012345680000,\n  0.0000015,\n  0.000001,\n  100,\n  100000000000000000000,\n  12345.678,\n  -2.5e+30,\n  5e-324,\n  1.7976931348623157e+308\n]\n")]
    [Arguments("[]", "[]\n")]
    [Arguments("{}", "{}\n")]
    [Arguments("[true,false,null,\"\"]", "[\n  true,\n  false,\n  null,\n  \"\"\n]\n")]
    public async Task MatchesTheSdkByteForByte(string input, string expected)
    {
        using var doc = JsonDocument.Parse(input);

        await Assert.That(CanonicalJson.Serialize(doc.RootElement)).IsEqualTo(expected);
    }

    [Test]
    public async Task StringEscaping_MatchesJsonStringify()
    {
        // JSON input escapes U+0001, \b \f \n \r \t, quote, backslash; then raw '/<>&', e-acute, an emoji,
        // DEL and U+2028 (JSON.stringify leaves the last two unescaped).
        var raw = "/<>&'" + (char)0xE9 + char.ConvertFromUtf32(0x1F600) + (char)0x7F + (char)0x2028;
        var input = "{\"s\":\"\\u0001\\b\\f\\n\\r\\t\\\"\\\\" + raw + "\"}";
        var expected = "{\n  \"s\": \"\\u0001\\b\\f\\n\\r\\t\\\"\\\\" + raw + "\"\n}\n";
        using var doc = JsonDocument.Parse(input);

        await Assert.That(CanonicalJson.Serialize(doc.RootElement)).IsEqualTo(expected);
    }

    [Test]
    public async Task SelfDeployFixture_CanonicalizesToItsOwnBytes()
    {
        using var doc = JsonDocument.Parse(Bytes);

        await Assert.That(CanonicalJson.Serialize(doc.RootElement)).IsEqualTo(Text);
    }

    [Test]
    public async Task ParsedFixture_WritesBackToItsExactBytes()
    {
        await Assert.That(ManifestWriter.ToCanonicalJson(Parse())).IsEqualTo(Text);
    }

    [Test]
    public async Task ManifestHash_IsSha256OfDescribeOutputBytes()
    {
        var expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Bytes));

        await Assert.That(ManifestVersioning.ManifestHash(Parse())).IsEqualTo(expected);
        await Assert.That(expected).IsEqualTo("sha256:907ab1b00cd090e79991b123b11ed083848bb828049bbe4ead996b51aba435cf");
    }

    [Test]
    public async Task ManifestHash_IgnoresKeyOrderAndWhitespace()
    {
        var shuffled = ReverseKeys(Node())!.ToJsonString(); // minified, every object's keys reversed
        await Assert.That(shuffled).IsNotEqualTo(Text);

        var m = ManifestParser.Parse(Encoding.UTF8.GetBytes(shuffled));
        m.TryPickProblems(out _, out var manifest);

        await Assert.That(ManifestVersioning.ManifestHash(manifest!)).IsEqualTo(ManifestVersioning.ManifestHash(Parse()));
    }
}
