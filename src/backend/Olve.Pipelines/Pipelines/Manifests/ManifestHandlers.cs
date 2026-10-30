using System.Text;
using Olve.TypeSpec;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.Pipelines.Manifests;

/// <summary>
/// <c>pl dev validate</c> (spec §7.1): runs the service's reconcile checks (<see cref="ManifestParser"/> +
/// <see cref="ManifestValidator"/>) on a posted manifest without persisting anything. Always 200 with the
/// report — an invalid manifest is a result, not a request error.
/// </summary>
[OperationHandler]
public sealed class ValidateManifestHandler : Contract.IManifestsValidateHandler
{
    public Task<Contract.ManifestsValidateResponse> HandleAsync(Contract.ManifestsValidateRequest request, CancellationToken cancellationToken)
        => Task.FromResult<Contract.ManifestsValidateResponse>(ManifestValidation.Validate(Encoding.UTF8.GetBytes(request.Body.GetRawText())));
}

public static class ManifestValidation
{
    public static Contract.ManifestValidation Validate(ReadOnlyMemory<byte> utf8)
    {
        if (!ManifestParser.TryParse(utf8, out var manifest, out var parseIssues))
            return new Contract.ManifestValidation
            {
                Valid = false,
                ManifestHash = null,
                Errors = [.. parseIssues.Where(i => i.Severity == ManifestIssueSeverity.Error).Select(ToApi)],
                Warnings = [.. parseIssues.Where(i => i.Severity == ManifestIssueSeverity.Warning).Select(ToApi)],
            };

        var report = ManifestValidator.Validate(manifest);
        return new Contract.ManifestValidation
        {
            Valid = report.IsValid,
            ManifestHash = ManifestVersioning.ManifestHash(manifest),
            Errors = [.. report.Errors.Select(ToApi)],
            Warnings = [.. report.Warnings.Select(ToApi)],
        };
    }

    private static Contract.ManifestIssue ToApi(ManifestIssue issue) => new()
    {
        Code = issue.Code,
        Severity = issue.Severity == ManifestIssueSeverity.Error ? Contract.ManifestIssueSeverity.Error : Contract.ManifestIssueSeverity.Warning,
        Message = issue.Message,
        Path = issue.Path,
        Step = issue.Step,
    };
}
