namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>One manifest problem as reported by the validate endpoint (<see cref="Severity"/>: <c>error</c> / <c>warning</c>).</summary>
public record ManifestIssueView(string Code, string Severity, string Message, string Path, string? Step)
{
    public static ManifestIssueView From(ManifestIssue issue) => new(issue.Code,
        issue.Severity == ManifestIssueSeverity.Error ? "error" : "warning", issue.Message, issue.Path, issue.Step);
}

/// <summary>The reconcile checks' verdict on a manifest. <see cref="ManifestHash"/> is set when it parses.</summary>
public record ManifestValidationResponse(bool Valid, string? ManifestHash, ManifestIssueView[] Errors, ManifestIssueView[] Warnings);

/// <summary>
/// <c>pl dev validate</c> (spec §7.1): runs the service's reconcile checks (<see cref="ManifestParser"/> +
/// <see cref="ManifestValidator"/>) on a posted manifest without persisting anything. Always 200 with the
/// report — an invalid manifest is a result, not a request error.
/// </summary>
public static class ManifestEndpoints
{
    public static void MapManifestEndpoints(this WebApplication app)
    {
        app.MapPost("/api/manifests/validate", async (HttpRequest request, CancellationToken ct) =>
            {
                // Read the raw bytes (bounded): the parser reports invalid JSON, duplicate keys and size itself.
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > ManifestParser.MaxBytes)
                        break;
                }

                return Validate(buffer.ToArray());
            })
            .Accepts<System.Text.Json.JsonElement>("application/json")
            .Produces<ManifestValidationResponse>()
            .WithName("ValidateManifest")
            .WithTags("Versions")
            .WithSummary("Validate a manifest")
            .WithDescription("Runs the reconcile checks on a manifest (`pl dev validate`). Always 200, with `valid`, `manifestHash`, `errors` and `warnings`.");
    }

    public static ManifestValidationResponse Validate(ReadOnlyMemory<byte> utf8)
    {
        if (!ManifestParser.TryParse(utf8, out var manifest, out var parseIssues))
            return new ManifestValidationResponse(false, null,
                parseIssues.Where(i => i.Severity == ManifestIssueSeverity.Error).Select(ManifestIssueView.From).ToArray(),
                parseIssues.Where(i => i.Severity == ManifestIssueSeverity.Warning).Select(ManifestIssueView.From).ToArray());

        var report = ManifestValidator.Validate(manifest);
        return new ManifestValidationResponse(report.IsValid, ManifestVersioning.ManifestHash(manifest),
            report.Errors.Select(ManifestIssueView.From).ToArray(),
            report.Warnings.Select(ManifestIssueView.From).ToArray());
    }
}
