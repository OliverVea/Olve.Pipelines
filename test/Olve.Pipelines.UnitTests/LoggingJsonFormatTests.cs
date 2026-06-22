using System.Text.Json;
using Microsoft.Extensions.Logging;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// Guards the load-bearing logging contract: structured message args (and the flattened
/// <see cref="ResultProblemLogExtensions.LogProblems"/> fields) reach the logger as discrete
/// key/value state — which the deployed <c>Logging:Console:FormatterName=json</c> formatter turns
/// into queryable JSON fields rather than an opaque string blob. We capture the log state directly
/// (TUnit forbids redirecting <c>Console.Out</c>) and confirm it serializes to JSON with each field
/// surfaced.
/// </summary>
public class LoggingJsonFormatTests
{
    /// <summary>Records the structured state of each log call so tests can inspect the emitted fields.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
                Entries.Add((logLevel, kvps));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    // Mirror what the JSON console formatter does: drop the raw template, keep the named fields.
    private static JsonElement ToJsonFields(IReadOnlyList<KeyValuePair<string, object?>> state)
    {
        var fields = state
            .Where(kvp => kvp.Key != "{OriginalFormat}")
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        return JsonDocument.Parse(JsonSerializer.Serialize(fields)).RootElement.Clone();
    }

    [Test]
    public async Task StructuredArgs_SurfaceAsDiscreteJsonFields()
    {
        var logger = new RecordingLogger();

        logger.LogInformation("Job '{JobId}' completed in {ElapsedMs}ms", "abc123", 42);

        var (level, state) = logger.Entries.Single();
        var json = ToJsonFields(state);

        await Assert.That(level).IsEqualTo(LogLevel.Information);
        await Assert.That(json.GetProperty("JobId").GetString()).IsEqualTo("abc123");
        await Assert.That(json.GetProperty("ElapsedMs").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task LogProblems_FlattensProblemSetIntoScalarJsonFields()
    {
        var logger = new RecordingLogger();
        var problems = new List<ResultProblem>
        {
            new("first problem") { Severity = 1 },
            new("second problem") { Severity = 5 },
        };

        logger.LogProblems(LogLevel.Warning, problems, "Operation '{Op}' failed", "deploy");

        var (level, state) = logger.Entries.Single();
        var json = ToJsonFields(state);

        await Assert.That(level).IsEqualTo(LogLevel.Warning);
        await Assert.That(json.GetProperty("Op").GetString()).IsEqualTo("deploy");
        await Assert.That(json.GetProperty("ProblemCount").GetInt32()).IsEqualTo(2);
        await Assert.That(json.GetProperty("MaxSeverity").GetInt32()).IsEqualTo(5);
        await Assert.That(json.GetProperty("ProblemMessages").GetString()!).Contains("first problem");
        await Assert.That(json.GetProperty("ProblemMessages").GetString()!).Contains("second problem");
    }
}
