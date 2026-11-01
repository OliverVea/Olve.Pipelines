using Olve.Pipelines.Shared.Problems;
using Olve.Results;
using TUnit.Assertions.Assertions;
using TUnit.Assertions.Conditions;
using TUnit.Assertions.Core;

namespace Olve.Pipelines.UnitTests;

internal static class ProblemAssertions
{
    /// <summary>Asserts the one problem's exact type (it decides the HTTP status) and the text a user reads.</summary>
    public static Task IsSingle<TProblem>(this ResultProblemCollection? problems, string message)
        where TProblem : ResultProblem => problems.IsSingle(typeof(TProblem), message);

    public static async Task IsSingle(this ResultProblemCollection? problems, Type type, string message)
    {
        await Assert.That(problems).IsNotNull().Because("the result succeeded");
        var problem = await Assert.That(problems!).HasSingleItem();
        await Assert.That(problem!.GetType()).IsEqualTo(type);
        await Assert.That(problem.ToDisplayString()).IsEqualTo(message);
    }

    /// <summary>Asserts the text of each of several problems, in order.</summary>
    public static async Task AreExactly(this ResultProblemCollection? problems, params string[] messages)
    {
        await Assert.That(problems).IsNotNull().Because("the result succeeded");
        await Assert.That(string.Join('\n', problems!.Select(p => p.ToDisplayString()))).IsEqualTo(string.Join('\n', messages));
    }

#pragma warning disable TUnitAssertions0015 // as in Olve.Results.TUnit, which has no DeletionResult overload
    public static PropertyAssertionResult<DeletionResult> Succeeded(this IAssertionSource<DeletionResult> source) =>
        source.HasProperty(x => x.Succeeded).IsEqualTo(true);
#pragma warning restore TUnitAssertions0015
}
