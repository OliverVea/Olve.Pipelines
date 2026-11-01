using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Olve.Pipelines.UnitTests.Recordings;

/// <summary>What the server and pl need from Dex, in the documents the pinned image really serves.</summary>
public class RecordedDexTests
{
    [Test]
    public async Task Discovery_HasWhatLoginNeeds()
    {
        var text = RecordingReplay.Get("dex", "discovery").Response.BodyText;

        var discovery = OpenIdConnectConfiguration.Create(text);

        await Assert.That(discovery.Issuer).IsEqualTo("http://localhost:5556/dex");
        await Assert.That(discovery.AuthorizationEndpoint).IsEqualTo("http://localhost:5556/dex/auth");
        await Assert.That(discovery.TokenEndpoint).IsEqualTo("http://localhost:5556/dex/token");
        await Assert.That(discovery.JwksUri).IsEqualTo("http://localhost:5556/dex/keys");
        await Assert.That(discovery.IdTokenSigningAlgValuesSupported).Contains("RS256");
        await Assert.That(discovery.ScopesSupported).Contains("groups");

        var raw = JsonNode.Parse(text)!;
        await Assert.That(raw["device_authorization_endpoint"]!.GetValue<string>()).IsEqualTo("http://localhost:5556/dex/device/code");
        await Assert.That(raw["code_challenge_methods_supported"]!.AsArray().Select(m => m!.GetValue<string>())).Contains("S256");
    }

    [Test]
    public async Task Keys_ParseIntoASigningKey()
    {
        var keys = new JsonWebKeySet(RecordingReplay.Get("dex", "keys").Response.BodyText);

        await Assert.That(keys.GetSigningKeys()).IsNotEmpty();
        await Assert.That(keys.Keys.Select(k => k.Alg)).Contains(SecurityAlgorithms.RsaSha256);
    }
}
