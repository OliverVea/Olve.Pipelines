using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Configuration;

public static class ServiceConfiguration
{
    public static void AddPipelineServices(this IServiceCollection services)
    {
        // Services that own an EntityStoreIndex are singletons: CreateIndex subscribes to the store,
        // so an index must be disposed or live as long as the store. A transient owner that never
        // disposes leaks one index per resolution (#26). Their dependencies must be singletons too.
        services.AddSingleton<EntityStore<Pipeline>>();
        services.AddSingleton<PipelineEvents>();
        services.AddSingleton<IRunOnStartup, PipelineEventRegistration>();

        // Binding webhook auto-registration. PublicBaseUrl is config-bound (Webhooks:PublicBaseUrl);
        // null/empty disables auto-registration but leaves the inbound receiver working.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new WebhookOptions(config["Webhooks:PublicBaseUrl"]);
        });
        services.AddSingleton<IPipelineSecretReader, KubernetesPipelineSecretReader>();
        services.AddSingleton<IGitHubClient, GitHubClient>();
        services.AddSingleton<EntityStore<PipelineConfigBinding>>();
        services.AddSingleton<PipelineConfigBindingService>();
        services.AddSingleton<PipelineConfigBindingCleanupService>();
        services.AddTransient<BindingWebhookReceiver>();
        services.AddSingleton<IRunOnStartup, PipelineConfigBindingEventRegistration>();
        // Binding webhook-mode deploy: auto-register/deregister the push hook.
        services.AddSingleton<BindingHookStateStore>();
        services.AddSingleton<BindingHookWorkQueue>();
        services.AddSingleton<IRunOnStartup, BindingWebhookEventRegistration>();
        services.AddHostedService<BindingHookRegistrationService>();
        services.AddHostedService<BindingHookPersistenceService>();
        services.AddSingleton<IConfigSource, GitHubConfigSource>();
        // Register as a singleton AND as the hosted service so the reconcile-now endpoint can
        // resolve the same instance (which owns the per-binding config ETag cache) to run an
        // off-schedule reconcile.
        services.AddSingleton<DeployPollService>();
        services.AddHostedService(sp => sp.GetRequiredService<DeployPollService>());
        services.AddTransient<IEnumerable<Job>>(_ => []);
        services.AddSingleton<EntityStore<Job>>();
        services.AddSingleton<IdProvider>();
        services.AddSingleton<JobEvents>();
        services.AddSingleton<JobService>();
        services.AddTransient<JobLogService>();
        services.AddTransient<JobObsoletionService>();
        services.AddTransient<JobCancellationService>();
        services.AddSingleton<NoOpJobExecutorPendingStore>();
        services.TryAddTransient<IJobExecutor, NoOpJobExecutor>();

        // Registry must be registered before JobPersistenceService so IHostedLifecycleService
        // reverse-order StoppingAsync drains live watchers before persistence flushes.
        services.AddSingleton<JobWatcherRegistry>();
        services.AddSingleton<IHostedLifecycleService>(sp => sp.GetRequiredService<JobWatcherRegistry>());
        services.AddHostedService<JobRunner>();
        services.AddTransient<JobQueueService>();
        services.AddSingleton<IRunOnStartup, JobEventRegistration>();
        services.AddHostedService<StartupRunner>();
        services.AddTransient<PipelineService>();
        services.AddTransient<PipelineSummaryService>();
        services.AddSingleton<ReconcilePauseState>();
        // PollInterval is config-bindable (Reconcile:PollIntervalSeconds) so integration tests can
        // drive reconcile fast; defaults stay generous for production (see ReconcileOptions).
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var seconds = config.GetValue<int?>("Reconcile:PollIntervalSeconds");
            return seconds is { } s
                ? new ReconcileOptions { PollInterval = TimeSpan.FromSeconds(s) }
                : new ReconcileOptions();
        });
        services.AddTransient<ReconcileCoordinator>();
        services.AddSingleton<IPersistenceReadiness, PersistenceReadiness>();
        services.AddHostedService<ConfigurationPersistenceService>();
        services.AddHostedService<JobPersistenceService>();
    }
}
