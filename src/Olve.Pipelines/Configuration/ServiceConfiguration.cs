using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Pipelines.Building;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Processing;
using Olve.Pipelines.Pipelines.Production;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Pipelines.Polling;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Sync.ConfigSource;
using Olve.Pipelines.Pipelines.Triggers;
using Olve.Pipelines.Shared.Persistence;

namespace Olve.Pipelines.Configuration;

public static class ServiceConfiguration
{
    public static void AddPipelineServices(this IServiceCollection services)
    {
        services.AddSingleton<EntityStore<Pipeline>>();
        services.AddSingleton<PipelineEvents>();
        services.AddSingleton<IRunOnStartup, PipelineEventRegistration>();
        services.AddSingleton<EntityStore<ProductionStep>>();
        services.AddSingleton<AttachmentStore<ProductionStep, StepConfiguration>>();
        services.AddSingleton<ProductionStepEvents>();
        services.AddSingleton<IRunOnStartup, ProductionStepEventRegistration>();
        services.AddTransient<ProductionStepService>();
        services.AddTransient<ProductionStepCleanupService>();
        services.AddSingleton<EntityStore<ProcessingStep>>();
        services.AddSingleton<AttachmentStore<ProcessingStep, StepConfiguration>>();
        services.AddSingleton<AttachmentStore<ProcessingStep, ProcessingStepPromotion>>();
        services.AddSingleton<ProcessingStepEvents>();
        services.AddSingleton<IRunOnStartup, ProcessingStepEventRegistration>();
        services.AddTransient<ProcessingStepService>();
        services.AddTransient<ProcessingStepCleanupService>();
        services.AddTransient<PromotionGateService>();
        services.AddSingleton<EntityStore<Trigger>>();
        services.AddSingleton<TriggerEvents>();
        services.AddSingleton<IRunOnStartup, TriggerEventRegistration>();
        services.AddTransient<TriggerService>();
        services.AddTransient<TriggerCleanupService>();
        services.AddTransient<TriggerExecutionService>();
        services.AddTransient<GitHubWebhookReceiver>();

        // GitHub webhook auto-registration. PublicBaseUrl is config-bound (Webhooks:PublicBaseUrl);
        // null/empty disables auto-registration but leaves the inbound receiver working.
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            return new WebhookOptions(config["Webhooks:PublicBaseUrl"]);
        });
        services.AddSingleton<IPipelineSecretReader, KubernetesPipelineSecretReader>();
        services.AddSingleton<IGitHubClient, GitHubClient>();
        services.AddSingleton<GitHubHookStateStore>();
        services.AddSingleton<GitHubHookWorkQueue>();
        services.AddSingleton<IRunOnStartup, GitHubWebhookEventRegistration>();
        services.AddHostedService<GitHubHookRegistrationService>();
        services.AddHostedService<GitHubHookPersistenceService>();
        services.AddSingleton<EntityStore<PipelineConfigBinding>>();
        services.AddTransient<PipelineConfigBindingService>();
        services.AddTransient<PipelineConfigBindingCleanupService>();
        services.AddTransient<BindingWebhookReceiver>();
        services.AddSingleton<IRunOnStartup, PipelineConfigBindingEventRegistration>();
        // Binding webhook-mode deploy: auto-register/deregister the push hook (reuses the GitHub
        // client + secret reader from the trigger webhook subsystem).
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
        services.AddHostedService<PollTriggerService>();
        services.AddTransient<IEnumerable<ArtifactBundle>>(_ => []);
        services.AddSingleton<EntityStore<ArtifactBundle>>();
        services.AddTransient<ArtifactBundleService>();
        services.AddTransient<IEnumerable<Job>>(_ => []);
        services.AddSingleton<EntityStore<Job>>();
        services.AddTransient<IEnumerable<JobGroup>>(_ => []);
        services.AddSingleton<EntityStore<JobGroup>>();
        services.AddSingleton<IdProvider>();
        services.AddSingleton<JobEvents>();
        services.AddTransient<JobService>();
        services.AddTransient<JobGroupService>();
        services.AddTransient<JobLogService>();
        services.AddTransient<JobObsoletionService>();
        services.AddTransient<JobCancellationService>();
        services.AddTransient<JobGroupCompletionService>();
        services.AddTransient<DownstreamTriggerService>();

        // Failure handlers: best-effort scripts run as untracked K8s Jobs when a job group fails.
        services.AddSingleton<AttachmentStore<Pipeline, FailureHandlers.FailureHandlerBindings>>();
        services.AddSingleton<FailureHandlers.FailureHandlerLibrary>();
        services.AddTransient<FailureHandlers.FailureHandlerBindingService>();
        services.AddTransient<FailureHandlers.FailureHandlerService>();
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
        services.AddTransient<PipelineDocumentBuilder>();
        services.AddTransient<ManifestCompiler>();
        services.AddTransient<PipelineReconciler>();
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
        services.AddHostedService<PromotionPersistenceService>();
        services.AddHostedService<BundlePersistenceService>();
        services.AddHostedService<JobPersistenceService>();
    }
}
