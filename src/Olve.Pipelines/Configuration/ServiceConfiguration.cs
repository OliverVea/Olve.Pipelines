using Microsoft.Extensions.DependencyInjection.Extensions;
using Olve.Pipelines.GitHub;
using Olve.Pipelines.Kubernetes;
using Olve.Pipelines.Jobs;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Failures;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Scheduling;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Shared;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Triggers;
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
        services.AddTransient<IConfigReconciler, PipelineReconcileService>();
        services.AddTransient<IPushTriggerHandler, PushTriggerHandler>();
        services.AddTransient<RunInputService>();
        services.AddTransient<TriggerMappingService>();
        services.AddHostedService<PollTriggerService>();
        // Register as a singleton AND as the hosted service so the reconcile-now endpoint can
        // resolve the same instance (which owns the per-binding config ETag cache) to run an
        // off-schedule reconcile.
        services.AddSingleton<DeployPollService>();
        services.AddHostedService(sp => sp.GetRequiredService<DeployPollService>());
        services.AddSingleton<IdProvider>();

        // v2 run domain: versions, activation (operational state), sequences, runs, attempts.
        services.AddSingleton<EntityStore<PipelineVersion>>();
        services.AddSingleton<AttachmentStore<Pipeline, PipelineActivation>>();
        services.AddSingleton<AttachmentStore<Pipeline, PipelineSequences>>();
        services.AddSingleton<AttachmentStore<Pipeline, EntityVersionMap>>();
        services.AddSingleton<PipelineNumberingService>();
        services.AddSingleton<PipelineVersionEvents>();
        services.AddSingleton<PipelineVersionService>();
        services.AddSingleton<PipelineActivationService>();
        services.AddTransient<PipelineVersionCleanupService>();
        services.AddSingleton<IRunOnStartup, PipelineVersionEventRegistration>();
        services.AddSingleton<EntityStore<Run>>();
        services.AddSingleton<EntityStore<Attempt>>();
        services.AddSingleton<RunEvents>();
        services.AddSingleton<AttemptEvents>();
        services.AddSingleton<RunService>();
        services.AddSingleton<AttemptService>();
        services.AddTransient<RunQueryService>();
        services.AddTransient<RunViewService>();
        services.AddTransient<AttemptFileService>();
        services.AddTransient<RunCleanupService>();
        services.AddSingleton<IRunOnStartup, RunEventRegistration>();

        // v2 scheduler: stage barriers, when/skip propagation, supersede, de-dup, reruns/continue,
        // promotion gates, draining queue + switch completion. One lock serialises it.
        services.AddSingleton<RunSchedulingLock>();
        services.AddSingleton<RunPlanCache>();
        services.AddSingleton<AttachmentStore<Pipeline, PipelineStepGates>>();
        services.AddSingleton<AttachmentStore<Pipeline, PendingRunStart>>();
        services.AddSingleton<AttachmentStore<Pipeline, PollTriggerStates>>();
        services.AddTransient<StepGateService>();
        services.AddTransient<StageScheduler>();
        services.AddTransient<RunStartService>();
        services.AddTransient<SwitchService>();
        services.AddTransient<RunContinuationService>();
        services.AddTransient<RunCancellationService>();
        services.AddTransient<AttemptLaunchService>();
        services.AddTransient<RunSchedulingHandler>();

        // Failure handlers (onFailure): a failed run dispatches its pinned manifest's handlers as untracked
        // harness Jobs (the K8s launcher replaces the no-op when Kubernetes is configured).
        services.AddSingleton(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var linkBase = config["Links:BaseUrl"];
            if (string.IsNullOrWhiteSpace(linkBase)) linkBase = config["Webhooks:PublicBaseUrl"];
            return new FailureHandlerOptions(linkBase ?? "");
        });
        services.AddTransient<FailureHandlerService>();
        services.TryAddSingleton<IFailureHandlerLauncher, NoOpFailureHandlerLauncher>();
        services.AddSingleton<IRunOnStartup, FailureHandlerEventRegistration>();

        // Job machinery: each attempt runs as one K8s Job.
        services.AddTransient<JobLogService>();
        services.AddTransient<JobObsoletionService>();
        services.AddSingleton<NoOpJobExecutorPendingStore>();
        services.TryAddTransient<IJobExecutor, NoOpJobExecutor>();
        services.TryAddTransient<IAttemptLiveLogs, NoAttemptLiveLogs>();

        // Registry must be registered before RunPersistenceService so IHostedLifecycleService
        // reverse-order StoppingAsync drains live watchers before persistence flushes.
        services.AddSingleton<JobWatcherRegistry>();
        services.AddSingleton<IHostedLifecycleService>(sp => sp.GetRequiredService<JobWatcherRegistry>());
        services.AddHostedService<JobRunner>();
        services.AddTransient<JobQueueService>();
        services.AddSingleton<IRunOnStartup, JobEventRegistration>();
        // After every event registration: the sweep's writes must reach cleanup/obsoletion/scheduling.
        services.AddSingleton<IRunOnStartup, RunSchedulingEventRegistration>();
        services.AddSingleton<IRunOnStartup, RunSchedulingStartup>();
        services.AddHostedService<StartupRunner>();
        services.AddTransient<PipelineService>();
        services.AddTransient<PipelineSummaryService>();
        services.AddTransient<PipelineHealthService>();
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
        services.AddSingleton<IPersistenceReadiness, PersistenceReadiness>();
        services.AddHostedService<ConfigurationPersistenceService>();
        services.AddHostedService<RunPersistenceService>();
    }
}
