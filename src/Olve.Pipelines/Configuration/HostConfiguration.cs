namespace Olve.Pipelines.Configuration;

public static class HostConfiguration
{
    public static void ConfigureHost(this WebApplicationBuilder builder, string[] args)
    {
        var environment = builder.Environment.EnvironmentName;

        builder.Configuration.Sources.Clear();
        builder.Configuration
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .AddCommandLine(args);

        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

        var host = builder.Configuration["Host"] ?? "localhost";
        var port = builder.Configuration["Port"] ?? "5000";
        builder.WebHost.UseUrls($"http://{host}:{port}");

        // For local dev, serve frontend from the Vite build output
        var frontendDist = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", "frontend", "dist"));
        if (Directory.Exists(frontendDist))
        {
            builder.Environment.WebRootPath = frontendDist;
            builder.Environment.WebRootFileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(frontendDist);
        }
    }
}
