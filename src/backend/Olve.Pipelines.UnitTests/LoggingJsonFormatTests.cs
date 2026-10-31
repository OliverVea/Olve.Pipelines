using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Olve.Pipelines.Configuration;
using Olve.Results;

namespace Olve.Pipelines.UnitTests;

/// <summary>
/// Real end-to-end guard for the logging contract: under a console setup that reproduces
/// <c>CreateSlimBuilder</c> (which pins <c>FormatterName="simple"</c>), the
/// <see cref="LoggingConfiguration.AddConfiguredConsoleFormatter"/> fix makes
/// <c>Logging:Console:FormatterName=json</c> take effect — so log lines are emitted as JSON and
/// structured args (incl. the flattened <see cref="ResultProblemLogExtensions.LogProblems"/> fields)
/// surface as queryable fields rather than a plain-text blob.
///
/// The formatter is chosen by name the way the console provider chooses it, and writes to a buffer
/// instead of the process-global console.
/// </summary>
public class LoggingJsonFormatTests
{
    private sealed class FormatterLogger(string category, ConsoleFormatter formatter, TextWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> format)
            => formatter.Write(new LogEntry<TState>(logLevel, category, eventId, state, exception, format), null, writer);
    }

    private static string CaptureConsole(string category, Action<ILogger> log)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Trace",
                ["Logging:Console:FormatterName"] = "json",
                ["Logging:Console:Json:UseUtcTimestamp"] = "true",
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddLogging(b =>
            {
                b.AddConfiguration(config.GetSection("Logging"));
                b.AddSimpleConsole();                      // reproduce the slim-builder default that hid the bug
                b.AddConfiguredConsoleFormatter(config);   // the actual fix under test
            })
            .BuildServiceProvider();

        var name = provider.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>().CurrentValue.FormatterName;
        var formatter = provider.GetRequiredService<IEnumerable<ConsoleFormatter>>().Single(f => f.Name == name);

        var buffer = new StringWriter();
        log(new FormatterLogger(category, formatter, buffer));
        return buffer.ToString();
    }

    private static JsonElement FindLine(string output, string category)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith('{')) continue;
            var element = JsonDocument.Parse(line).RootElement; // throws if a line isn't valid JSON
            if (element.TryGetProperty("Category", out var cat) && cat.GetString() == category)
                return element.Clone();
        }

        throw new InvalidOperationException($"No JSON log line for category '{category}' in console output:\n{output}");
    }

    [Test]
    public async Task JsonFormatterName_EmitsValidJsonWithStructuredArgsAsFields()
    {
        const string category = "JsonShapeTest";
        var output = CaptureConsole(category, logger =>
            logger.LogInformation("Job '{JobId}' completed in {ElapsedMs}ms", "abc123", 42));

        var root = FindLine(output, category);

        await Assert.That(root.GetProperty("LogLevel").GetString()).IsEqualTo("Information");
        await Assert.That(root.GetProperty("Message").GetString()).IsEqualTo("Job 'abc123' completed in 42ms");

        // The args must be individually queryable JSON fields, not folded into the message string.
        var state = root.GetProperty("State");
        await Assert.That(state.GetProperty("JobId").GetString()).IsEqualTo("abc123");
        await Assert.That(state.GetProperty("ElapsedMs").GetInt32()).IsEqualTo(42);
    }

    [Test]
    public async Task LogProblems_EmitsFlattenedScalarFieldsAsJson()
    {
        const string category = "LogProblemsTest";
        var problems = new List<ResultProblem>
        {
            new("first problem") { Severity = 1 },
            new("second problem") { Severity = 5 },
        };

        var output = CaptureConsole(category, logger =>
            logger.LogProblems(LogLevel.Warning, problems, "Operation '{Op}' failed", "deploy"));

        var root = FindLine(output, category);
        var state = root.GetProperty("State");

        await Assert.That(root.GetProperty("LogLevel").GetString()).IsEqualTo("Warning");
        await Assert.That(state.GetProperty("Op").GetString()).IsEqualTo("deploy");
        await Assert.That(state.GetProperty("ProblemCount").GetInt32()).IsEqualTo(2);
        await Assert.That(state.GetProperty("MaxSeverity").GetInt32()).IsEqualTo(5);
        await Assert.That(state.GetProperty("ProblemMessages").GetString()!).Contains("first problem");
        await Assert.That(state.GetProperty("ProblemMessages").GetString()!).Contains("second problem");
    }
}
