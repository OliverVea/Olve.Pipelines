using Microsoft.Extensions.Logging.Console;

namespace Olve.Pipelines.Configuration;

public static class LoggingConfiguration
{
    /// <summary>
    /// Makes <c>Logging:Console:FormatterName</c> actually take effect under
    /// <see cref="WebApplication.CreateSlimBuilder(string[])"/>.
    /// </summary>
    public static void ConfigureLogging(this WebApplicationBuilder builder)
        => builder.Logging.AddConfiguredConsoleFormatter(builder.Configuration);

    /// <summary>
    /// Re-applies the configured console formatter name so a deployed <c>FormatterName=json</c> selects
    /// the JSON formatter.
    ///
    /// <para><b>Why this is needed:</b> <c>CreateSlimBuilder</c> wires <c>AddSimpleConsole()</c>, which
    /// registers every built-in console formatter (including JSON) <i>but</i> pins
    /// <see cref="ConsoleLoggerOptions.FormatterName"/> to <c>"simple"</c> via a configure callback that
    /// runs after — and therefore overrides — the <c>Logging:Console</c> config binding. The result is
    /// that <c>FormatterName=json</c> is silently ignored and logs stay plain-text. Calling
    /// <c>AddConsole()</c> guarantees the JSON formatter is registered, and re-applying the config value
    /// <b>last</b> wins over the slim builder's pin. When unset (local dev) the simple/pretty default is
    /// left untouched.</para>
    ///
    /// Split out as an <see cref="ILoggingBuilder"/> seam so it can be unit-tested without booting the app.
    /// </summary>
    public static ILoggingBuilder AddConfiguredConsoleFormatter(this ILoggingBuilder logging, IConfiguration configuration)
    {
        logging.AddConsole();
        logging.Services.Configure<ConsoleLoggerOptions>(options =>
        {
            if (configuration["Logging:Console:FormatterName"] is { Length: > 0 } name)
                options.FormatterName = name;
        });
        return logging;
    }
}
