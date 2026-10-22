using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using static Olve.Pipelines.Cli.Commands.Runs.RunCommandSupport;

namespace Olve.Pipelines.Cli.Commands.Secrets;

/// <summary>
/// <c>pl secret copy &lt;source&gt;[/&lt;name&gt;] &lt;target&gt;</c> — copy one (or every) secret of a
/// pipeline to another pipeline on the same instance. The copy happens server-side: the value never
/// reaches this machine.
/// </summary>
public sealed class SecretCopyCommand : ICliCommand
{
    public IReadOnlySet<string> ValueOptions { get; } = CliCommand.Options("name");
    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "overwrite" };

    public string Noun => "secret";
    public string Verb => "copy";
    public int RequiredOperands => 2;
    public IReadOnlySet<string> BooleanFlags => Flags;
    public string HelpLine => "Copy a secret (or all) from another pipeline, server-side";
    public string? HelpDetail =>
        """
        pl secret copy <source-pipeline>[/<name>] <target-pipeline> [options]

          Copies a secret value from one pipeline to another on the same instance. The copy is
          done by the service; the value is never returned or printed. Without /<name> every
          secret of the source is copied. Pipelines by id or name.

          Caveat: anyone who can write the target pipeline can read the copied value from its steps.

          --name <target-name>   Store the copy under a different name (single secret only)
          --overwrite            Replace secrets the target already has (default: refuse)

          Examples:
            pl secret copy "Olve.Pipelines v2"/GITHUB_TOKEN my-app
            pl secret copy shared-creds my-app --overwrite
            pl secret copy my-app/GITHUB_TOKEN my-app --name GH_READ_TOKEN
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var (sourceText, name) = SplitSource(cli.Operand(0)!);
        var targetText = cli.Operand(1)!;
        var targetName = cli.Option("name");

        if (targetName is not null && name is null)
            return new ResultProblem("--name needs a single source secret (<source-pipeline>/<name>).");

        if ((await ResolvePipelineAsync(ctx, sourceText, ct)).TryPickProblems(out var sourceProblems, out var source))
            return sourceProblems;
        if ((await ResolvePipelineAsync(ctx, targetText, ct)).TryPickProblems(out var targetProblems, out var target))
            return targetProblems;

        var request = new CopySecretsRequest
        {
            SourcePipelineId = source.Id,
            Names = name is null ? null : [name],
            TargetName = targetName,
            Overwrite = cli.HasFlag("overwrite"),
        };

        if ((await ctx.Api.CopySecrets(target.Id.ToString(), request, ct)).TryPickProblems(out var problems, out var response))
            return problems;

        ctx.Output.Emit(response, CliJsonContext.Default.CopySecretsResponse, () =>
        {
            if (response.Copied.Length == 0)
                ctx.Output.Line($"Nothing to copy from {source.Label}.");
            else
                ctx.Output.Line($"Copied {string.Join(", ", response.Copied)} from {source.Label} to {target.Label}.");
        });
        return Result.Success();
    }

    /// <summary>Split <c>pipeline/name</c> on the last slash (secret names have none); no slash = all secrets.</summary>
    public static (string Pipeline, string? Name) SplitSource(string text)
    {
        var slash = text.LastIndexOf('/');
        return slash <= 0 || slash == text.Length - 1
            ? (text.TrimEnd('/'), null)
            : (text[..slash], text[(slash + 1)..]);
    }
}
