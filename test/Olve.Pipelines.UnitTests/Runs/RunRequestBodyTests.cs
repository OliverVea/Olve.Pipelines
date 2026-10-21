using System.Text.Json;
using Olve.Pipelines.Pipelines.Runs;

namespace Olve.Pipelines.UnitTests.Runs;

/// <summary>
/// The typed request bodies of TriggerRun/ContinueRun keep the wire shape (the input object itself, and
/// <c>{input: {...}}</c>) while giving OpenAPI a free-form object generated clients can send.
/// </summary>
public class RunRequestBodyTests
{
    // What the endpoints bind with: web defaults + the app's source-gen context (JsonConfiguration).
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { TypeInfoResolver = AppJsonContext.Default };

    private static T? Bind<T>(string body) => JsonSerializer.Deserialize<T>(body, Web);

    [Test]
    public async Task TriggerRunRequest_CapturesEveryTopLevelField()
    {
        var request = Bind<TriggerRunRequest>("""{"sha":"abc","deployTo":"beta","n":{"x":[1,null]},"z":null}""")!;

        var obj = JsonFields.ToJsonObject(request.Fields)!;

        await Assert.That(obj.ToJsonString()).IsEqualTo("""{"sha":"abc","deployTo":"beta","n":{"x":[1,null]},"z":null}""");
    }

    [Test]
    public async Task TriggerRunRequest_EmptyObject_IsEmptyInput()
    {
        var request = Bind<TriggerRunRequest>("{}")!;

        await Assert.That(JsonFields.ToJsonObject(request.Fields)!.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("\"x\"")]
    [Arguments("42")]
    public async Task TriggerRunRequest_NonObjectBody_FailsBinding(string body)
    {
        // Minimal APIs turn this into a 400 before the handler runs.
        await Assert.That(() => Bind<TriggerRunRequest>(body)).Throws<JsonException>();
    }

    [Test]
    public async Task ContinueRunRequest_InputIsAnObjectOfChanges()
    {
        var request = Bind<ContinueRunRequest>("""{"input":{"deployTo":"beta"}}""")!;

        await Assert.That(JsonFields.ToJsonObject(request.Input)!.ToJsonString()).IsEqualTo("""{"deployTo":"beta"}""");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"input":null}""")]
    public async Task ContinueRunRequest_NoInput_MeansNoChanges(string body)
    {
        var request = Bind<ContinueRunRequest>(body)!;

        await Assert.That(JsonFields.ToJsonObject(request.Input)).IsNull();
    }
}
