using System.Globalization;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines.Failures;

/// <summary>
/// Assembles the <c>FailureContext</c> a failure handler receives (sdk <c>library/models.ts</c>):
/// <c>pipeline {id, name}</c>, <c>run {id, number, address, versionStamp, trigger, url}</c>,
/// <c>version {number, commit}</c>, the run's <c>input</c>, a one-line <c>reason</c> and
/// <c>failedSteps[] {step, attempt, address, reason, logUrl}</c>. Addresses follow spec §5.5
/// (<c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;@&lt;attempt&gt;</c>); links point at the run API under
/// <paramref name="linkBase"/> (relative when empty). Pure.
/// </summary>
public static class FailureContextBuilder
{
    public static JsonObject Build(
        Pipeline pipeline, Run run, PipelineVersion version, IReadOnlyList<Attempt> failedAttempts, string linkBase)
    {
        var pipelineId = pipeline.Id.Value.Value.ToString("D", CultureInfo.InvariantCulture);
        var runAddress = $"{pipelineId}#{run.Number.ToString(CultureInfo.InvariantCulture)}";
        var runUrl = $"{linkBase.TrimEnd('/')}/api/pipelines/{pipelineId}/runs/{run.Number.ToString(CultureInfo.InvariantCulture)}";

        var failedSteps = new JsonArray();
        foreach (var attempt in failedAttempts)
        {
            var number = attempt.Number.ToString(CultureInfo.InvariantCulture);
            failedSteps.Add((JsonNode)new JsonObject
            {
                ["step"] = attempt.StepName,
                ["attempt"] = attempt.Number,
                ["address"] = $"{runAddress}/{attempt.StepName}@{number}",
                ["reason"] = Reason(attempt),
                ["logUrl"] = $"{runUrl}/steps/{attempt.StepName}/logs?attempt={number}",
            });
        }

        return new JsonObject
        {
            ["pipeline"] = new JsonObject { ["id"] = pipelineId, ["name"] = pipeline.Name },
            ["run"] = new JsonObject
            {
                ["id"] = run.Id.Value.Value.ToString("D", CultureInfo.InvariantCulture),
                ["number"] = run.Number,
                ["address"] = runAddress,
                ["versionStamp"] = run.VersionStamp,
                ["trigger"] = run.Trigger.Kind.ToString().ToLowerInvariant(),
                ["url"] = runUrl,
            },
            ["version"] = new JsonObject { ["number"] = version.Number, ["commit"] = version.CommitSha },
            ["input"] = ParseInput(run.Input),
            ["reason"] = Summary(failedAttempts),
            ["failedSteps"] = failedSteps,
        };
    }

    /// <summary><c>step 'echo' failed: …</c>, or <c>2 steps failed: echo (…); count (…)</c>.</summary>
    public static string Summary(IReadOnlyList<Attempt> failedAttempts) => failedAttempts.Count switch
    {
        0 => "the run failed",
        1 => $"step '{failedAttempts[0].StepName}' failed: {Reason(failedAttempts[0])}",
        var n => $"{n} steps failed: " + string.Join("; ", failedAttempts.Select(a => $"{a.StepName} ({Reason(a)})")),
    };

    private static string Reason(Attempt attempt)
        => attempt.Status is AttemptStatus.Failed { Reason: { Length: > 0 } reason } ? reason : "failed";

    private static JsonNode? ParseInput(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
