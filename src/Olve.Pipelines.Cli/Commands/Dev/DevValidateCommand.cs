using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;

namespace Olve.Pipelines.Cli.Commands.Dev;

/// <summary>
/// <c>pl dev validate</c> (spec §7.1) — describes the local pipeline from source, then posts the manifest
/// to <c>POST /api/manifests/validate</c> for the service's reconcile checks, without pushing.
/// </summary>
public sealed class DevValidateCommand(IProcessRunner runner, DevToolchain? toolchain = null, DevConsole? console = null) : ICliCommand
{
    /// <summary>Exit code for an invalid manifest — the same as describe-time problems.</summary>
    public const int InvalidExitCode = 78;

    public string Noun => "dev";
    public string Verb => "validate";
    public string HelpLine => "Describe locally, then run the service's reconcile checks on the manifest";

    public string? HelpDetail =>
        """
        pl dev validate [--dir <path>] [--json]

          Runs describe on the pipeline source (like `pl dev describe`) and posts the manifest to
          the service's validation endpoint, which runs the same checks reconcile does — without
          pushing or persisting anything. Errors exit 78; warnings alone exit 0. Needs a session
          (`pl login`) against the instance whose checks you want.

          --dir <path>   Pipeline directory (default: .pipelines)
          --json         Print the validation response as JSON
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var con = console ?? new DevConsole(ctx.Json);
        if ((await DevCommandSupport.OpenAsync(runner, toolchain, con, cli, ctx, ct)).TryPickProblems(out var problems, out var dev))
            return problems;
        if (dev is null)
            return Result.Success(); // describe failed; already reported with its exit code

        if ((await ctx.Api.ValidateManifest(dev.Manifest.Text, ct)).TryPickProblems(out var validateProblems, out var report))
            return validateProblems;

        ctx.Output.Emit(report, CliJsonContext.Default.ManifestValidationResponse, () => Render(ctx, dev.Manifest.Name, report));
        if (!report.Valid)
            ctx.ExitCode = InvalidExitCode;
        return Result.Success();
    }

    private static void Render(CommandContext ctx, string name, ManifestValidationResponse report)
    {
        ctx.Output.Line(report.Valid
            ? $"{name}: valid ({report.ManifestHash})"
            : $"{name}: invalid — {report.Errors.Length} error(s)");
        foreach (var issue in report.Errors)
            ctx.Output.Line($"  error   {Format(issue)}");
        foreach (var issue in report.Warnings)
            ctx.Output.Line($"  warning {Format(issue)}");
    }

    private static string Format(ManifestIssueView issue)
    {
        var where = issue.Step is { Length: > 0 } step ? $"{step}: " : "";
        var path = issue.Path is { Length: > 0 } p ? $" ({p})" : "";
        return $"[{issue.Code}] {where}{issue.Message}{path}";
    }
}
