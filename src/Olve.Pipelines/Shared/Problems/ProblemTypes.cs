using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Olve.Pipelines.Shared.Problems;

/// <summary>The request is valid but the current state forbids it (HTTP 409).</summary>
[DebuggerDisplay("{ToString()}")]
public sealed class ConflictProblem([StringSyntax(StringSyntaxAttribute.CompositeFormat)] string message, params object[] args)
    : ResultProblem(message, args)
{
    /// <summary>The request field the conflict is about (e.g. <c>input.sha</c>), if any.</summary>
    public string? Field { get; init; }

    public static ConflictProblem ForField(string field,
        [StringSyntax(StringSyntaxAttribute.CompositeFormat)] string message, params object[] args)
        => new(message, args) { Field = field };

    public override string ToString() => ProblemText.WithField(Field, FormattedMessage);
}

/// <summary>A request field is invalid (HTTP 400), with the field's path when known.</summary>
[DebuggerDisplay("{ToString()}")]
public sealed class ValidationProblem([StringSyntax(StringSyntaxAttribute.CompositeFormat)] string message, params object[] args)
    : ResultProblem(message, args)
{
    /// <summary>The path of the invalid field (e.g. <c>input.sha</c>, <c>status</c>), if any.</summary>
    public string? Field { get; init; }

    public static ValidationProblem ForField(string field,
        [StringSyntax(StringSyntaxAttribute.CompositeFormat)] string message, params object[] args)
        => new(message, args) { Field = field };

    public override string ToString() => ProblemText.WithField(Field, FormattedMessage);
}

/// <summary>A dependency (Garage, Kubernetes, GitHub) failed transiently; retrying may help (HTTP 503).</summary>
[DebuggerDisplay("{ToString()}")]
public sealed class UnavailableProblem : ResultProblem
{
    public UnavailableProblem([StringSyntax(StringSyntaxAttribute.CompositeFormat)] string message, params object[] args)
        : base(message, args) => IsRetryable = true;

    /// <summary>The exception is logged, never returned to the caller.</summary>
    public UnavailableProblem(Exception exception,
        [StringSyntax(StringSyntaxAttribute.CompositeFormat)] string message, params object[] args)
        : base(exception, message, args) => IsRetryable = true;
}

public static class ProblemText
{
    /// <summary>The message, prefixed by the field path when the problem has one. Brace-safe.</summary>
    public static string ToDisplayString(this ResultProblem problem) => WithField(FieldOf(problem), problem.FormattedMessage);

    public static string WithField(string? field, string text) => field is null ? text : $"{field}: {text}";

    /// <summary>The field path of a problem that carries one.</summary>
    public static string? FieldOf(ResultProblem problem) => problem switch
    {
        ValidationProblem v => v.Field,
        ConflictProblem c => c.Field,
        _ => null,
    };
}
