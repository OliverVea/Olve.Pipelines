using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
/// These capture the real console output (the only way to verify the actual formatter), so they
/// redirect the process-global <c>Console.Out</c>: <c>[NotInParallel]</c> serializes them, output is
/// restored in <c>finally</c>, and lines are matched by category to ignore any stray output.
/// TUnit0055 (warns that overriding Console can disrupt TUnit logging) is suppressed deliberately.
/// </summary>
[NotInParallel("console-capture")]
public class LoggingJsonFormatTests
{
#pragma warning disable TUnit0055 // Overwriting the Console writer — intentional and restored in finally.
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

        var original = Console.Out;
        var buffer = new StringWriter();
        // The console provider captures Console.Out at construction, so redirect before building it.
        Console.SetOut(buffer);
        try
        {
            using (var factory = LoggerFactory.Create(b =>
            {
                b.AddConfiguration(config.GetSection("Logging"));
                b.AddSimpleConsole();                      // reproduce the slim-builder default that hid the bug
                b.AddConfiguredConsoleFormatter(config);   // the actual fix under test
            }))
            {
                log(factory.CreateLogger(category));
            } // dispose joins the async console processor thread, flushing all queued lines
        }
        finally
        {
            Console.SetOut(original);
        }

        return buffer.ToString();
    }
#pragma warning restore TUnit0055

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
