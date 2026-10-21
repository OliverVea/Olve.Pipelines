namespace Olve.Pipelines.Kubernetes.Harness;

public static class HarnessConfiguration
{
    /// <summary>
    /// Registers <see cref="HarnessOptions"/> (from the <c>Harness</c> section, runtime class from
    /// <c>Kubernetes:RuntimeClassName</c>), <see cref="HarnessJobFactory"/> and <see cref="HarnessResultCollector"/>.
    /// The factory needs <c>S3UrlPresigner</c> and the collector <c>IAmazonS3</c>, both registered by storage config.
    /// </summary>
    public static void AddHarness(this IServiceCollection services, IConfiguration configuration, string? runtimeClassName)
    {
        var section = configuration.GetSection("Harness");
        var defaults = new HarnessOptions();

        string Str(string key, string fallback) => string.IsNullOrWhiteSpace(section[key]) ? fallback : section[key]!;
        string? Opt(string key) => string.IsNullOrWhiteSpace(section[key]) ? null : section[key];
        long Long(string key, long fallback) => section.GetValue<long?>(key) ?? fallback;

        var options = defaults with
        {
            HarnessBaseImage = Str(nameof(HarnessOptions.HarnessBaseImage), defaults.HarnessBaseImage),
            BunImage = Str(nameof(HarnessOptions.BunImage), defaults.BunImage),
            RuntimeClassName = runtimeClassName,
            DefaultStepTimeoutSeconds = Long(nameof(HarnessOptions.DefaultStepTimeoutSeconds), defaults.DefaultStepTimeoutSeconds),
            MaxStepTimeoutSeconds = Long(nameof(HarnessOptions.MaxStepTimeoutSeconds), defaults.MaxStepTimeoutSeconds),
            StartupGraceSeconds = Long(nameof(HarnessOptions.StartupGraceSeconds), defaults.StartupGraceSeconds),
            UrlExpiryGraceSeconds = Long(nameof(HarnessOptions.UrlExpiryGraceSeconds), defaults.UrlExpiryGraceSeconds),
            ScratchSizeLimit = Str(nameof(HarnessOptions.ScratchSizeLimit), defaults.ScratchSizeLimit),
            StepCpuRequest = Opt(nameof(HarnessOptions.StepCpuRequest)),
            StepMemoryRequest = Opt(nameof(HarnessOptions.StepMemoryRequest)),
            StepMemoryLimit = Opt(nameof(HarnessOptions.StepMemoryLimit)),
            BuildTimeoutSeconds = Long(nameof(HarnessOptions.BuildTimeoutSeconds), defaults.BuildTimeoutSeconds),
            BuildScratchSizeLimit = Str(nameof(HarnessOptions.BuildScratchSizeLimit), defaults.BuildScratchSizeLimit),
            BuildCpuRequest = Str(nameof(HarnessOptions.BuildCpuRequest), defaults.BuildCpuRequest),
            BuildMemoryRequest = Str(nameof(HarnessOptions.BuildMemoryRequest), defaults.BuildMemoryRequest),
            BuildMemoryLimit = Str(nameof(HarnessOptions.BuildMemoryLimit), defaults.BuildMemoryLimit),
            BuildTokenSecretKey = Str(nameof(HarnessOptions.BuildTokenSecretKey), defaults.BuildTokenSecretKey),
        };

        services.AddSingleton(options);
        services.AddTransient<HarnessJobFactory>();
        services.AddTransient<HarnessResultCollector>();
        services.AddTransient<IHarnessStorage, S3HarnessStorage>();
        services.AddTransient<HarnessJobControl>();
    }
}
