using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.UnitTests.Manifests;

namespace Olve.Pipelines.UnitTests.Scheduling;

public class AttemptInputBuilderTests
{
    private sealed class FakePool(string input, Dictionary<string, PoolEntry> entries) : IRunPool
    {
        public JsonElement Input { get; } = JsonDocument.Parse(input).RootElement.Clone();
        public PoolEntry Get(string stepName) => entries.TryGetValue(stepName, out var e) ? e : new PoolEntry.Missing();
    }

    private static JsonElement El(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static StepDescriptor Step(Dictionary<string, Mapping> input, Dictionary<string, InputType>? types = null)
        => new("s", StepKinds.Custom, "alpine", types ?? [], input, null, [], "sha256:x", null, null, null);

    [Test]
    public async Task Builds_RefsConstsAndNestedObjects_LeavingOutUnresolvedPaths()
    {
        var pool = new FakePool("""{"sha":"abc","deployTo":"beta"}""", new()
        {
            ["build"] = new PoolEntry.Produced(El("""{"image":{"path":"i.tar"},"version":"7"}""")),
        });
        var step = Step(new()
        {
            ["whole"] = new RefMapping("build"),
            ["field"] = new RefMapping("build.version"),
            ["meta"] = new ObjectMapping(new Dictionary<string, Mapping>
            {
                ["sha"] = new RefMapping("input.sha"),
                ["missing"] = new RefMapping("input.nope"),
                ["fixed"] = new ConstMapping(El("""[1,true,"x"]""")),
            }),
            ["gone"] = new RefMapping("build.nope.deeper"),
        });

        var result = AttemptInputBuilder.Build(step, pool);

        await Assert.That(result).IsTypeOf<AttemptInputResult.Built>();
        var built = (AttemptInputResult.Built)result;
        await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(built.Json), JsonNode.Parse(
            """{"whole":{"image":{"path":"i.tar"},"version":"7"},"field":"7","meta":{"sha":"abc","fixed":[1,true,"x"]}}"""))).IsTrue();
        await Assert.That(built.Hash).IsEqualTo(CanonicalJson.Hash(El(built.Json)));
    }

    [Test]
    public async Task Hash_IsIndependentOfKeyOrder()
    {
        var pool = new FakePool("""{"a":1,"b":2}""", []);
        var ab = AttemptInputBuilder.Build(Step(new() { ["a"] = new RefMapping("input.a"), ["b"] = new RefMapping("input.b") }), pool);
        var ba = AttemptInputBuilder.Build(Step(new() { ["b"] = new RefMapping("input.b"), ["a"] = new RefMapping("input.a") }), pool);

        await Assert.That(((AttemptInputResult.Built)ab).Hash).IsEqualTo(((AttemptInputResult.Built)ba).Hash);
    }

    [Test]
    public async Task SkippedProducer_RequiredInputIsUnavailable_OptionalIsLeftOut()
    {
        var pool = new FakePool("{}", new() { ["prod"] = new PoolEntry.Skipped("when deployTo ∉ [prod]") });
        var types = new Dictionary<string, InputType> { ["p"] = new ModelInputType("ProdDeployment", Optional: true) };

        var optional = AttemptInputBuilder.Build(Step(new() { ["p"] = new RefMapping("prod") }, types), pool);
        var required = AttemptInputBuilder.Build(Step(new() { ["p"] = new RefMapping("prod") }), pool);
        var missing = AttemptInputBuilder.Build(Step(new() { ["q"] = new RefMapping("never") }), pool);

        await Assert.That(((AttemptInputResult.Built)optional).Json).IsEqualTo("{}");
        await Assert.That(((AttemptInputResult.Unavailable)required).Reason).IsEqualTo("input 'p' needs prod (skipped: when deployTo ∉ [prod])");
        await Assert.That(((AttemptInputResult.Unavailable)missing).Reason).IsEqualTo("input 'q' needs never, which has no successful attempt");
    }

    [Test]
    public async Task ConsumedInputFields_ListsInputRefs_WholeInputAsStar()
    {
        var step = Step(new()
        {
            ["a"] = new RefMapping("input.sha"),
            ["b"] = new ObjectMapping(new Dictionary<string, Mapping> { ["c"] = new RefMapping("input.repo.owner") }),
            ["d"] = new RefMapping("build"),
            ["e"] = new RefMapping("input"),
        });

        await Assert.That(AttemptInputBuilder.ConsumedInputFields(step).ToList()).IsEquivalentTo(["sha", "repo", "*"]);
    }

    [Test]
    public async Task FileRefs_AreDetectedByShape_Deduplicated()
    {
        var refJson = SchedulerScenario.FileRef(3, "build", 2, "chart/");
        var other = SchedulerScenario.FileRef(3, "build", 2, "i.tar");
        var refs = AttemptFileRefs.Extract("{\"a\":" + refJson + ",\"list\":[" + refJson + ",{\"run\":1}],\"x\":{\"y\":" + other + "}}");

        await Assert.That(refs.Select(r => r.FileKey).ToList()).IsEquivalentTo(["3/build/2/chart/", "3/build/2/i.tar"]);
        await Assert.That(refs[0].JsonPath).IsEqualTo("$.a");
        await Assert.That(refs[1].ObjectPath).IsEqualTo("i.tar");
    }

    [Test]
    public async Task When_EvaluatesAllConditions_EmptyMatchesAlways()
    {
        var plan = RunPlan.From(ManifestFixture.Parse());

        await Assert.That(plan.Stages.Select(st => string.Join(",", st.Steps.Select(x => x.Name))).ToList())
            .IsEquivalentTo(["checkout", "build,test", "deploy-beta", "deploy-prod", "publish-cli", "summary"]);
        var publish = plan.Stages[4];
        await Assert.That(publish.Conditions).Count().IsEqualTo(2);
        await Assert.That(publish.EvaluateWhen(El("""{"deployTo":"prod","publishCli":true}"""))).IsNull();
        await Assert.That(publish.EvaluateWhen(El("""{"deployTo":"beta","publishCli":true}"""))).IsEqualTo("when deployTo ∉ [prod]");
        await Assert.That(publish.EvaluateWhen(El("""{"deployTo":"prod"}"""))).IsEqualTo("when publishCli ∉ [true]");
        await Assert.That(plan.Stages[0].EvaluateWhen(El("{}"))).IsNull();
    }
}
