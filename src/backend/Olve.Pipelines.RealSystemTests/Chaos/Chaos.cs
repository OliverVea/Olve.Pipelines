using System.Text.Json.Nodes;

namespace Olve.Pipelines.RealSystemTests.Chaos;

/// <summary>
/// What the chaos tests share. <c>CHAOS_SEEDS</c> replays seeds, otherwise each session walks every moment
/// <c>CHAOS_ROUNDS</c> times (default 1) from a random seed. <c>CHAOS_CONTROL=1</c> swaps a test's fault
/// for its harmless twin, at the same moment: what fails under a control isn't the fault's doing.
/// </summary>
public static class Chaos
{
    public const string Message = "survives the fault";

    private static readonly Lazy<IReadOnlyList<int>> SessionSeeds = new(() =>
    {
        if (Environment.GetEnvironmentVariable("CHAOS_SEEDS") is { Length: > 0 } listed)
            return listed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();

        var rounds = int.TryParse(Environment.GetEnvironmentVariable("CHAOS_ROUNDS"), out var n) ? n : 1;
        var start = Random.Shared.Next(1_000_000);
        return Enumerable.Range(start, rounds * SeededFault.Moments.Count).ToList();
    });

    public static IEnumerable<int> Seeds() => SessionSeeds.Value;

    public static bool Control => Environment.GetEnvironmentVariable("CHAOS_CONTROL") == "1";

    /// <summary>A pipeline with its secret set, saved, so a fault can only lose the run's own state.</summary>
    public static async Task<TestPipeline> NewPipelineAsync(TestServer server, string hint)
    {
        var pipeline = await server.NewPipelineAsync(hint);
        await server.Api.SetSecretAsync(pipeline.Id, "FIXTURE_SECRET", "s3cret");
        await WaitUntilPersistedAsync(server, pipeline.Id.ToString());
        return pipeline;
    }

    /// <summary>Waits for <paramref name="text"/> to reach the persisted indexes.</summary>
    public static async Task WaitUntilPersistedAsync(TestServer server, string text)
    {
        string[] keys = ["pipeline-versions.json", "pipeline-state.json"];
        await Wait.UntilAsync(
            async () => (await Task.WhenAll(keys.Select(server.Bucket.ReadAsync))).Count(t => t?.Contains(text, StringComparison.OrdinalIgnoreCase) == true),
            found => found == keys.Length, Wait.Cluster, $"{text} to reach {string.Join(", ", keys)}");
    }

    /// <summary>The fault may beat the reply; then the run was never promised to anyone.</summary>
    public static async Task<bool> TryStartRunAsync(TestServer server, TestPipeline pipeline)
    {
        try
        {
            await server.StartRunAsync(pipeline, new JsonObject { ["message"] = Message });
            return true;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public static void FailOn(IReadOnlyList<string> violations, string fault, int seed)
    {
        if (violations.Count == 0)
            return;
        var test = TestContext.Current?.Metadata.TestDetails.ClassType.Name;
        Assert.Fail($"{fault}\n- {string.Join("\n- ", violations)}\nReplay: CHAOS_SEEDS={seed} tools/real-system/host.sh chaos <slot> {test}");
    }
}
