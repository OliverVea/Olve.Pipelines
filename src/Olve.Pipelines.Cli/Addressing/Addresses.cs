using System.Text.RegularExpressions;
using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;

namespace Olve.Pipelines.Cli.Addressing;

/// <summary>
/// A run-scoped address (spec §5.5): <c>&lt;pipeline&gt;#&lt;run&gt;[/&lt;step&gt;[@&lt;attempt&gt;][/&lt;path&gt;]]</c>.
/// <see cref="Pipeline"/> is a pipeline id or name (resolved separately by <see cref="PipelineResolver"/>).
/// Without an attempt, a step address means the latest successful attempt (output/files) or the latest
/// attempt (logs) — the server decides.
/// </summary>
public sealed partial record RunAddress(string Pipeline, int Run, string? Step = null, int? Attempt = null, string? Path = null)
{
    // Lazy pipeline part: the first "#<digits>" followed by "/" or the end splits it, so a file path
    // containing '#' stays in the path. Step names are DNS labels on the server; here anything but '/' and '@'.
    [GeneratedRegex(@"^(?<pipeline>.+?)#(?<run>\d+)(?:/(?<step>[^/@]+)(?:@(?<attempt>[^/]*))?(?:/(?<path>.+))?)?$")]
    private static partial Regex Pattern();

    /// <summary>Parses any run address; use <see cref="ParseRun"/> / <see cref="ParseStep"/> to require a shape.</summary>
    public static Result<RunAddress> Parse(string text)
    {
        var match = Pattern().Match(text);
        if (!match.Success)
            return new ResultProblem(
                "Expected an address like <pipeline>#<run>[/<step>[@<attempt>]] (e.g. olve-pipelines#412/deploy-beta@1), got '{0}'.", text);

        if (!int.TryParse(match.Groups["run"].Value, out var run) || run < 1)
            return new ResultProblem("Run number in '{0}' must be a positive integer.", text);

        int? attempt = null;
        if (match.Groups["attempt"].Success)
        {
            if (!int.TryParse(match.Groups["attempt"].Value, out var n) || n < 1)
                return new ResultProblem("Attempt in '{0}' must be a positive integer (e.g. @1).", text);
            attempt = n;
        }

        var pipeline = match.Groups["pipeline"].Value.Trim();
        if (pipeline.Length == 0)
            return new ResultProblem("Address '{0}' has no pipeline.", text);

        return new RunAddress(
            pipeline,
            run,
            match.Groups["step"].Success ? match.Groups["step"].Value : null,
            attempt,
            match.Groups["path"].Success ? match.Groups["path"].Value : null);
    }

    /// <summary>Requires exactly <c>&lt;pipeline&gt;#&lt;run&gt;</c>.</summary>
    public static Result<RunAddress> ParseRun(string text)
    {
        if (Parse(text).TryPickProblems(out var problems, out var address))
            return problems;
        if (address.Step is not null)
            return new ResultProblem("Expected a run address <pipeline>#<run>, got the step address '{0}'.", text);
        return address;
    }

    /// <summary>Requires <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;[@&lt;attempt&gt;]</c>; a trailing path only when <paramref name="allowPath"/>.</summary>
    public static Result<RunAddress> ParseStep(string text, bool allowPath = false)
    {
        if (Parse(text).TryPickProblems(out var problems, out var address))
            return problems;
        if (address.Step is null)
            return new ResultProblem("Expected a step address <pipeline>#<run>/<step>[@<attempt>], got '{0}'.", text);
        if (address.Path is not null && !allowPath)
            return new ResultProblem("Unexpected file path in step address '{0}'.", text);
        return address;
    }

    public override string ToString() => Format(Pipeline, Run, Step, Attempt, Path);

    /// <summary>Formats an address; pipeline names are shown as given (quote it in a shell when it has spaces).</summary>
    public static string Format(string pipeline, int run, string? step = null, int? attempt = null, string? path = null)
    {
        var text = $"{pipeline}#{run}";
        if (step is null)
            return text;
        text += $"/{step}";
        if (attempt is { } n)
            text += $"@{n}";
        if (path is not null)
            text += $"/{path}";
        return text;
    }
}

/// <summary>A pipeline-scoped step reference <c>&lt;pipeline&gt;/&lt;step&gt;</c> (gates and history — keyed on step name, not a run).</summary>
public sealed record StepRef(string Pipeline, string Step)
{
    /// <summary>Splits on the last '/' (step names never contain one; pipeline names might).</summary>
    public static Result<StepRef> Parse(string text)
    {
        var slash = text.LastIndexOf('/');
        if (slash <= 0 || slash == text.Length - 1)
            return new ResultProblem("Expected <pipeline>/<step> (e.g. olve-pipelines/deploy-prod), got '{0}'.", text);
        var pipeline = text[..slash].Trim();
        var step = text[(slash + 1)..];
        if (pipeline.Length == 0)
            return new ResultProblem("'{0}' has no pipeline.", text);
        if (pipeline.Contains('#'))
            return new ResultProblem("Expected <pipeline>/<step> without a run number, got '{0}'.", text);
        return new StepRef(pipeline, step);
    }

    public override string ToString() => $"{Pipeline}/{Step}";
}

/// <summary>Resolves a pipeline given by id or name to its id (names: exact match first, then a unique case-insensitive one).</summary>
public static class PipelineResolver
{
    public static async Task<Result<Pipeline>> ResolveAsync(IPipelinesApi api, string pipeline, CancellationToken ct)
    {
        if (Guid.TryParse(pipeline, out var id))
            return new Pipeline { Id = id, Name = pipeline };

        if ((await api.ListPipelines(ct)).TryPickProblems(out var problems, out var all))
            return problems;
        return Match(all, pipeline);
    }

    public static Result<Pipeline> Match(IReadOnlyList<Pipeline> pipelines, string name)
    {
        var exact = pipelines.Where(p => p.Name == name).ToList();
        if (exact.Count == 1)
            return exact[0];

        var candidates = exact.Count > 1
            ? exact
            : pipelines.Where(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

        return candidates.Count switch
        {
            1 => candidates[0],
            0 => new ResultProblem("No pipeline named '{0}'. Run `pl pipeline list`, or pass the pipeline id.", name),
            _ => new ResultProblem("Pipeline name '{0}' is ambiguous ({1}); pass the pipeline id.",
                name, string.Join(", ", candidates.Select(p => p.Id))),
        };
    }
}
