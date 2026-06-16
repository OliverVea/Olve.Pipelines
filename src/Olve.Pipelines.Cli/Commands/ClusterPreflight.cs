namespace Olve.Pipelines.Cli.Commands;

/// <summary>
/// Shared cluster preflight used by install and uninstall: verifies kubectl, helm, and that
/// the cluster is reachable before either command starts mutating it.
/// </summary>
public static class ClusterPreflight
{
    public static async Task<Result> RunAsync(IProcessRunner processRunner, CancellationToken ct)
    {
        Console.WriteLine("==> Preflight: checking kubectl, helm, and cluster access");

        if ((await processRunner.RunCheckedAsync("kubectl", ["version", "--client"], ct: ct)).TryPickProblems(out var kp))
            return kp;
        if ((await processRunner.RunCheckedAsync("helm", ["version", "--short"], ct: ct)).TryPickProblems(out var hp))
            return hp;

        var context = await processRunner.RunAsync("kubectl", ["config", "current-context"], ct: ct);
        if (!context.TryPickProblems(out _, out var ctx) && ctx.Succeeded)
            Console.WriteLine($"  context: {ctx.StandardOutput.Trim()}");

        if ((await processRunner.RunCheckedAsync("kubectl", ["cluster-info"], ct: ct)).TryPickProblems(out var cip))
            return new ResultProblem("Cluster is not reachable: {0}", string.Join("; ", cip.Select(p => p.ToString())));

        return Result.Success();
    }
}
