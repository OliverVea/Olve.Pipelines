using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Results;

namespace Olve.Pipelines.UnitTests.Http;

/// <summary>
/// A TestServer with the real error pipeline (<see cref="ApiErrorMiddleware"/>, <see cref="ApiResultMapping"/>)
/// and stub endpoints that fail in each way. Not the real Program: that would load user secrets and start the
/// deploy loops. JSON resolves through <see cref="AppJsonContext"/> only, so a missing registration fails here.
/// </summary>
internal sealed class ApiErrorHost : IAsyncDisposable
{
    public const string Secret = "s3cr3t-internal-detail";

    private static readonly JsonElement ShaSchema = JsonDocument.Parse("""
        {"type":"object","properties":{"sha":{"type":"string","pattern":"^[0-9a-f]{7,40}$"}},"required":["sha"]}
        """).RootElement.Clone();

    private readonly WebApplication _app;

    private ApiErrorHost(WebApplication app, RecordingLoggerProvider logs)
    {
        _app = app;
        Logs = logs;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }
    public RecordingLoggerProvider Logs { get; }

    public static async Task<ApiErrorHost> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        var logs = new RecordingLoggerProvider();
        builder.Logging.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Information);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.TypeInfoResolverChain.Clear();
            o.SerializerOptions.TypeInfoResolverChain.Add(AppJsonContext.Default);
        });
        builder.Services.AddApiErrors();

        var app = builder.Build();
        app.UseApiErrors();

        app.MapGet("/api/t/ok", Result<string> () => "fine").WithApiResultMapping<string>();
        app.MapGet("/api/t/missing", Result<string> () =>
                Result.Failure<string>(new ResultProblemCollection([new NotFoundProblem("Run #{0} not found.", 7)])
                    .Prepend("Could not cancel run #{0}.", 7)))
            .WithApiResultMapping<string>();
        app.MapPost("/api/t/conflict", Result () =>
                ConflictProblem.ForField("input.sha", "Field '{0}' cannot change: completed step(s) {1} consumed it.", "sha", "build"))
            .WithApiResultMapping();
        app.MapPost("/api/t/invalid", Result<string> () =>
                JsonSchemaValidator.Validate(ShaSchema, JsonNode.Parse("""{"sha":"XYZ"}""")).Map(_ => "unreachable"))
            .WithApiResultMapping<string>();
        app.MapGet("/api/t/unavailable", Result () =>
                new UnavailableProblem(new HttpRequestException(Secret), "Kubernetes is unreachable."))
            .WithApiResultMapping();
        app.MapGet("/api/t/throw", Result () => throw new InvalidOperationException(Secret)).WithApiResultMapping();
        app.MapGet("/api/t/throw-transient", Result () => throw new HttpRequestException(Secret)).WithApiResultMapping();
        app.MapGet("/api/t/number/{n:int}", Result<string> (int n, int limit) => n.ToString()).WithApiResultMapping<string>();
        app.MapPost("/api/t/body", Result<string> ([FromBody] Dictionary<string, string> body) => body.Count.ToString())
            .WithApiResultMapping<string>();
        app.MapDelete("/api/t/gone", DeletionResult () => DeletionResult.NotFound()).WithDeletionMapping();

        await app.StartAsync();
        return new ApiErrorHost(app, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

/// <summary>Records log entries with their scope values (e.g. the hosting scope's TraceId).</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;
    public void Dispose() { }

    public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception, IReadOnlyDictionary<string, string?> Scope);

    private sealed class Logger(RecordingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => provider._scopes.Push(state);
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var scope = new Dictionary<string, string?>();
            provider._scopes.ForEachScope((s, d) =>
            {
                if (s is IEnumerable<KeyValuePair<string, object?>> pairs)
                    foreach (var (k, v) in pairs) d[k] = v?.ToString();
            }, scope);
            provider.Entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception, scope));
        }
    }
}
