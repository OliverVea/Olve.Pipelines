using System.Text.Json.Nodes;
using Olve.Pipelines.Cli.Addressing;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Commands.Runs;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Cli;

public class AddressTests
{
    [Test]
    [Arguments("olve-pipelines#412", "olve-pipelines", 412, null, null, null)]
    [Arguments("olve-pipelines#412/deploy-beta", "olve-pipelines", 412, "deploy-beta", null, null)]
    [Arguments("olve-pipelines#412/deploy-beta@1", "olve-pipelines", 412, "deploy-beta", 1, null)]
    [Arguments("olve-pipelines#412/build/image.tar", "olve-pipelines", 412, "build", null, "image.tar")]
    [Arguments("v2 test fixture#4/hello@2/dir/a#1/b.txt", "v2 test fixture", 4, "hello", 2, "dir/a#1/b.txt")]
    [Arguments("ad01bc3f-992f-4695-8bce-6389b63ce23f#7/last", "ad01bc3f-992f-4695-8bce-6389b63ce23f", 7, "last", null, null)]
    [Arguments("weird#name#3/x", "weird#name", 3, "x", null, null)]
    public async Task Parse_Valid(string text, string pipeline, int run, string? step, int? attempt, string? path)
    {
        var result = RunAddress.Parse(text);

        await Assert.That(result.TryPickProblems(out _, out var address)).IsFalse();
        await Assert.That(address!).IsEqualTo(new RunAddress(pipeline, run, step, attempt, path));
        await Assert.That(address!.ToString()).IsEqualTo(text);
    }

    [Test]
    [Arguments("olve-pipelines")]
    [Arguments("olve-pipelines#")]
    [Arguments("olve-pipelines#x")]
    [Arguments("olve-pipelines#0")]
    [Arguments("#3")]
    [Arguments("olve-pipelines#3/")]
    [Arguments("olve-pipelines#3/step@")]
    [Arguments("olve-pipelines#3/step@0")]
    [Arguments("olve-pipelines#3/step@x")]
    [Arguments("olve-pipelines/step")]
    public async Task Parse_Invalid(string text)
    {
        await Assert.That(RunAddress.Parse(text).Succeeded).IsFalse();
    }

    [Test]
    public async Task ParseRun_RejectsStep_ParseStep_RequiresStep_AndPathOnlyWhenAllowed()
    {
        await Assert.That(RunAddress.ParseRun("p#3").Succeeded).IsTrue();
        await Assert.That(RunAddress.ParseRun("p#3/s").Succeeded).IsFalse();
        await Assert.That(RunAddress.ParseStep("p#3").Succeeded).IsFalse();
        await Assert.That(RunAddress.ParseStep("p#3/s@2").Succeeded).IsTrue();
        await Assert.That(RunAddress.ParseStep("p#3/s/f.txt").Succeeded).IsFalse();
        await Assert.That(RunAddress.ParseStep("p#3/s/f.txt", allowPath: true).Succeeded).IsTrue();
    }

    [Test]
    [Arguments("olve-pipelines/deploy-prod", "olve-pipelines", "deploy-prod")]
    [Arguments("v2 test fixture/last", "v2 test fixture", "last")]
    [Arguments("team/app/deploy", "team/app", "deploy")]
    public async Task StepRef_Valid(string text, string pipeline, string step)
    {
        await Assert.That(StepRef.Parse(text).TryPickProblems(out _, out var stepRef)).IsFalse();
        await Assert.That(stepRef!).IsEqualTo(new StepRef(pipeline, step));
    }

    [Test]
    [Arguments("olve-pipelines")]
    [Arguments("olve-pipelines/")]
    [Arguments("/deploy")]
    [Arguments("olve-pipelines#3/deploy")]
    public async Task StepRef_Invalid(string text)
    {
        await Assert.That(StepRef.Parse(text).Succeeded).IsFalse();
    }

    private static readonly Pipeline[] Pipelines =
    [
        new() { Id = Guid.Parse("11111111-1111-1111-1111-111111111111"), Name = "v2 test fixture" },
        new() { Id = Guid.Parse("22222222-2222-2222-2222-222222222222"), Name = "Olve.Pipelines" },
        new() { Id = Guid.Parse("33333333-3333-3333-3333-333333333333"), Name = "dup" },
        new() { Id = Guid.Parse("44444444-4444-4444-4444-444444444444"), Name = "DUP" },
    ];

    [Test]
    public async Task Resolver_Exact_ThenCaseInsensitive_AmbiguousAndMissingFail()
    {
        await Assert.That(PipelineResolver.Match(Pipelines, "v2 test fixture").TryPickProblems(out _, out var exact)).IsFalse();
        await Assert.That(exact!.Id).IsEqualTo(Pipelines[0].Id);

        await Assert.That(PipelineResolver.Match(Pipelines, "olve.pipelines").TryPickProblems(out _, out var folded)).IsFalse();
        await Assert.That(folded!.Id).IsEqualTo(Pipelines[1].Id);

        // exact wins over a case-folded duplicate; a folded-only match with two candidates is ambiguous
        await Assert.That(PipelineResolver.Match(Pipelines, "dup").TryPickProblems(out _, out var dup)).IsFalse();
        await Assert.That(dup!.Id).IsEqualTo(Pipelines[2].Id);
        await Assert.That(PipelineResolver.Match(Pipelines, "Dup").TryPickProblems(out var ambiguous, out _)).IsTrue();
        await Assert.That(ambiguous!.First().ToString()).Contains("ambiguous");

        await Assert.That(PipelineResolver.Match(Pipelines, "nope").Succeeded).IsFalse();
    }

    [Test]
    public async Task Resolver_Guid_SkipsTheLookup()
    {
        var api = new FakePipelinesApi();

        var result = await PipelineResolver.ResolveAsync(api, "11111111-1111-1111-1111-111111111111", CancellationToken.None);

        await Assert.That(result.Succeeded).IsTrue();
        await Assert.That(api.Calls).IsEmpty();
    }

    private static readonly JsonObject Schema = (JsonObject)JsonNode.Parse("""
        { "type": "object",
          "properties": { "sha": { "type": "string" }, "mode": { "type": "string", "enum": ["normal", "skip-last"] },
                          "replicas": { "type": "integer" } },
          "required": ["sha"] }
        """)!;

    [Test]
    public async Task BuildInput_TypesFieldsBySchema_OverAnInputBase()
    {
        var result = RunCommandSupport.BuildInput("""{"mode":"normal","replicas":1}""", ["sha=1234567", "replicas=3"], Schema);

        await Assert.That(result.TryPickProblems(out _, out var input)).IsFalse();
        // an all-digit sha stays a string because the schema says so; no defaults are added
        await Assert.That(input!.ToJsonString()).IsEqualTo("""{"mode":"normal","replicas":3,"sha":"1234567"}""");
    }

    [Test]
    public async Task BuildInput_Problems()
    {
        await Assert.That(RunCommandSupport.BuildInput(null, ["nope=1"], Schema).Succeeded).IsFalse();
        await Assert.That(RunCommandSupport.BuildInput(null, ["replicas=many"], Schema).Succeeded).IsFalse();
        await Assert.That(RunCommandSupport.BuildInput(null, ["novalue"], Schema).Succeeded).IsFalse();
        await Assert.That(RunCommandSupport.BuildInput("[1]", [], Schema).Succeeded).IsFalse();
        await Assert.That(RunCommandSupport.BuildInput("{oops", [], Schema).Succeeded).IsFalse();
    }

    [Test]
    public async Task BuildInput_WithoutSchema_IsUntyped()
    {
        var result = RunCommandSupport.BuildInput(null, ["a=1", "b=text"], schema: null);

        await Assert.That(result.TryPickProblems(out _, out var input)).IsFalse();
        await Assert.That(input!.ToJsonString()).IsEqualTo("""{"a":1,"b":"text"}""");
    }
}
