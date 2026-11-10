using System.Text.Json;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// The links a Waiting attempt shows: for each external input with a <see cref="ExternalLink"/>, the URL in the value of
/// the regular input it names, read from the attempt's <c>input.json</c>. The handler's links join them once it has run.
/// </summary>
public static class WaitingLinks
{
    /// <summary>
    /// The resolved links of <paramref name="step"/> (null when none). A link that does not resolve is left out and
    /// logged as a warning; the step waits all the same.
    /// </summary>
    public static IReadOnlyList<AttemptLink>? For(StepDescriptor step, string? inputJson, Run run, string stepName, ILogger logger)
    {
        var linked = step.ExternalInputs.Where(e => e.Value.Link is not null).ToList();
        if (linked.Count == 0)
            return null;

        JsonElement? input = null;
        if (inputJson is not null && JsonText.IsWellFormed(inputJson))
        {
            using var doc = JsonDocument.Parse(inputJson);
            input = doc.RootElement.Clone();
        }

        var links = new List<AttemptLink>();
        foreach (var (key, external) in linked)
        {
            if (Resolve(external, input).TryPickProblems(out var problems, out var link))
                logger.LogProblems(LogLevel.Warning, problems,
                    "No waiting link for external input '{InputName}' of step '{StepName}' in run #{RunNumber} ('{RunId}')",
                    key, stepName, run.Number, run.Id);
            else
                links.Add(link);
        }

        return links.Count == 0 ? null : links.DistinctBy(l => l.Url, StringComparer.Ordinal).ToList();
    }

    /// <summary>The link of <paramref name="external"/>, from the attempt's input object.</summary>
    public static Result<AttemptLink> Resolve(ExternalInput external, JsonElement? input)
    {
        if (external.Link is not { } link)
            return new ResultProblem("The external input has no link.");
        if (input is not { ValueKind: JsonValueKind.Object } root)
            return new ResultProblem("The attempt's input is not a JSON object.");
        if (!root.TryGetProperty(link.Input, out var current) || current.ValueKind == JsonValueKind.Null)
            return new ResultProblem("Input '{0}' has no value.", link.Input);

        var at = link.Path is null ? link.Input : $"{link.Input}.{link.Path}";
        foreach (var segment in link.Path?.Split('.') ?? [])
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return new ResultProblem("'{0}' is not in the input.", at);
        }

        var url = current.ValueKind == JsonValueKind.String ? current.GetString()! : null;
        if (url is null || url.Length > AttemptLink.MaxUrlLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return new ResultProblem("'{0}' is not an absolute http(s) URL of at most {1} characters.", at, AttemptLink.MaxUrlLength);

        var label = link.Label ?? external.Title;
        return new AttemptLink(url, label.Length > AttemptLink.MaxLabelLength ? label[..AttemptLink.MaxLabelLength] : label);
    }
}
