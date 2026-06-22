using Olve.Results;

// Deliberately in Microsoft.Extensions.Logging so LogProblems is available at every ILogger call
// site through the web SDK's implicit usings — no per-file using needed.
namespace Microsoft.Extensions.Logging;

/// <summary>
/// Logging helper that flattens a <see cref="ResultProblem"/> set into scalar, queryable fields
/// (<c>ProblemCount</c> / <c>ProblemMessages</c> / <c>MaxSeverity</c>) instead of letting the
/// collection <c>ToString()</c> itself into an opaque blob under a single <c>{Problems}</c>
/// placeholder — which the JSON console formatter would emit as one unsearchable string.
/// </summary>
public static class ResultProblemLogExtensions
{
    /// <summary>
    /// Logs <paramref name="message"/> at <paramref name="level"/>, appending the problem set as the
    /// structured fields <c>{ProblemCount}</c>, <c>{ProblemMessages}</c> (brief messages joined by
    /// "; ") and <c>{MaxSeverity}</c>. The caller's own template placeholders and <paramref name="args"/>
    /// come first, so existing message templates carry over unchanged (minus the trailing
    /// <c>: {Problems}</c>).
    /// </summary>
    public static void LogProblems(
        this ILogger logger,
        LogLevel level,
        IEnumerable<ResultProblem> problems,
        string message,
        params object?[] args)
    {
        if (!logger.IsEnabled(level)) return;

        var list = problems as IReadOnlyList<ResultProblem> ?? problems.ToArray();

        var count = list.Count;
        var messages = string.Join("; ", list.Select(p => p.ToBriefString()));
        var maxSeverity = count == 0 ? 0 : list.Max(p => p.Severity);

        var allArgs = new object?[args.Length + 3];
        args.CopyTo(allArgs, 0);
        allArgs[args.Length] = count;
        allArgs[args.Length + 1] = messages;
        allArgs[args.Length + 2] = maxSeverity;

        logger.Log(
            level,
            message + " ProblemCount={ProblemCount} ProblemMessages={ProblemMessages} MaxSeverity={MaxSeverity}",
            allArgs);
    }
}
