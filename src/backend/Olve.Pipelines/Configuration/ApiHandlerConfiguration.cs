using Olve.Pipelines.Api;
using Olve.Pipelines.Health;
using Olve.Pipelines.Kubernetes.Api;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Configs;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Sync;
using Olve.Pipelines.Pipelines.Versions;
using Olve.Pipelines.Streaming;

namespace Olve.Pipelines.Configuration;

public static class ApiHandlerConfiguration
{
    /// <summary>One handler per contract operation (<c>src/spec/</c>); <c>UsePipelinesApi</c> refuses to start without all of them.</summary>
    public static void AddApiHandlers(this IServiceCollection services)
    {
        services.AddSingleton<IInstanceHealthHandler, HealthHandler>();
        services.AddSingleton<IInstanceReadyHandler, ReadinessHandler>();
        services.AddSingleton<IInstanceFrontendConfigHandler, FrontendConfigHandler>();
        services.AddTransient<IPipelinesListHandler, ListPipelinesHandler>();
        services.AddTransient<IPipelinesGetHandler, GetPipelineHandler>();
        services.AddTransient<IPipelinesDeleteHandler, DeletePipelineHandler>();
        services.AddTransient<IPipelinesHealthHandler, GetPipelineHealthHandler>();
        services.AddTransient<IVersionsListHandler, ListVersionsHandler>();
        services.AddTransient<IVersionsGetHandler, GetVersionHandler>();
        services.AddTransient<IVersionsEffectiveHandler, GetEffectivePipelineHandler>();
        services.AddTransient<IVersionsSwitchHandler, SwitchVersionHandler>();
        services.AddTransient<ISecretsListHandler, ListSecretsHandler>();
        services.AddTransient<ISecretsSetHandler, SetSecretHandler>();
        services.AddTransient<ISecretsDeleteHandler, DeleteSecretHandler>();
        services.AddTransient<ISecretsCopyHandler, CopySecretsHandler>();
        services.AddTransient<IConfigGetHandler, GetConfigHandler>();
        services.AddTransient<IConfigSetHandler, SetConfigHandler>();
        services.AddTransient<IConfigHistoryHandler, ListConfigSnapshotsHandler>();
        services.AddTransient<IConfigRollbackHandler, RollbackConfigHandler>();
        services.AddTransient<IConfigCheckHandler, CheckConfigHandler>();
        services.AddTransient<IBindingsCreateHandler, CreatePipelineHandler>();
        services.AddTransient<IBindingsGetHandler, GetBindingHandler>();
        services.AddTransient<IBindingsStatusHandler, GetBindingStatusHandler>();
        services.AddTransient<IBindingsUpdateHandler, UpdateBindingHandler>();
        services.AddTransient<IBindingsSetBranchHandler, SetBindingBranchHandler>();
        services.AddTransient<IBindingsSetPollingHandler, SetBindingPollingHandler>();
        services.AddTransient<IBindingsReconcileHandler, ReconcileBindingHandler>();
        services.AddTransient<IStepsBlockedHandler, ListBlockedStepsHandler>();
        services.AddTransient<IStepsBlockHandler, BlockStepHandler>();
        services.AddTransient<IStepsUnblockHandler, UnblockStepHandler>();
        services.AddTransient<IStepsHistoryHandler, GetStepHistoryHandler>();
        services.AddTransient<IRunsListHandler, ListRunsHandler>();
        services.AddTransient<IRunsStartHandler, StartRunHandler>();
        services.AddTransient<IRunsCriticalPathHandler, GetCriticalPathHandler>();
        services.AddTransient<IRunsGetHandler, GetRunHandler>();
        services.AddTransient<IRunsCancelHandler, CancelRunHandler>();
        services.AddTransient<IRunsRerunHandler, RerunRunHandler>();
        services.AddTransient<IRunsContinueHandler, ContinueRunHandler>();
        services.AddTransient<IRunsAttemptsHandler, ListRunAttemptsHandler>();
        services.AddTransient<IRunStepsAttemptsHandler, ListStepAttemptsHandler>();
        services.AddTransient<IRunStepsRerunHandler, RerunStepHandler>();
        services.AddTransient<IRunStepsCancelHandler, CancelStepHandler>();
        services.AddTransient<IRunStepsAnswerHandler, AnswerStepInputHandler>();
        services.AddTransient<IRunStepsDeclineHandler, DeclineStepInputHandler>();
        services.AddTransient<IRunStepsFilesHandler, ListStepFilesHandler>();
        services.AddTransient<IRunStepsOutputHandler, GetStepOutputHandler>();
        services.AddTransient<IRunStepsLogsHandler, GetStepLogsHandler>();
        services.AddTransient<IAttemptsListHandler, ListActiveAttemptsHandler>();
        services.AddTransient<IRunStepsLogStreamHandler, StreamStepLogsHandler>();
        services.AddTransient<IEventsStreamHandler, StreamEventsHandler>();
        services.AddTransient<IManifestsValidateHandler, ValidateManifestHandler>();
        services.AddHttpContextAccessor(); // the audit actor of secret copies and config saves
    }

    /// <summary>
    /// Maps the contract's operations and applies what the contract doesn't carry yet: the auth
    /// policy and the OpenAPI tag and summary of each (api.json feeds the API reference until it
    /// is generated from the spec, docs/design/spec-first.md). Writes need a signed-in user (the
    /// fallback policy); reads the read policy.
    /// </summary>
    public static void MapApi(this WebApplication app)
    {
        var api = app.UsePipelinesApi();
        api.InstanceHealth.AllowAnonymous().WithTags("Instance").WithSummary("Liveness");
        api.InstanceReady.AllowAnonymous().WithTags("Instance").WithSummary("Readiness");
        api.InstanceFrontendConfig.AllowAnonymous().WithTags("Instance").WithSummary("Get the login settings");
        api.PipelinesList.RequireRead().WithTags("Pipelines").WithSummary("List pipelines");
        api.PipelinesGet.RequireRead().WithTags("Pipelines").WithSummary("Get a pipeline");
        api.PipelinesDelete.WithTags("Pipelines").WithSummary("Delete a pipeline");
        api.PipelinesHealth.RequireRead().WithTags("Pipelines").WithSummary("Get a pipeline's health");
        api.VersionsList.RequireRead().WithTags("Versions").WithSummary("List versions");
        api.VersionsGet.RequireRead().WithTags("Versions").WithSummary("Get a version");
        api.VersionsEffective.RequireRead().WithTags("Versions").WithSummary("Get a version's effective pipeline");
        api.VersionsSwitch.WithTags("Versions").WithSummary("Switch the active version");
        // Secrets are never a read: listing them stays behind a signed-in user even with anonymous reads.
        api.SecretsList.WithTags("Secrets").WithSummary("List secrets");
        api.SecretsSet.WithTags("Secrets").WithSummary("Set a secret");
        api.SecretsDelete.WithTags("Secrets").WithSummary("Delete a secret");
        api.SecretsCopy.WithTags("Secrets").WithSummary("Copy secrets from another pipeline");
        api.ConfigGet.RequireRead().WithTags("Config").WithSummary("Get the config document");
        api.ConfigSet.WithTags("Config").WithSummary("Save the config document");
        api.ConfigHistory.RequireRead().WithTags("Config").WithSummary("List config snapshots");
        api.ConfigRollback.WithTags("Config").WithSummary("Roll back the config");
        api.ConfigCheck.RequireRead().WithTags("Config").WithSummary("Check the config");
        api.BindingsCreate.WithTags("Bindings").WithSummary("Create a pipeline bound to a repo");
        api.BindingsGet.RequireRead().WithTags("Bindings").WithSummary("Get the binding");
        api.BindingsStatus.RequireRead().WithTags("Bindings").WithSummary("Get the binding's reconcile status");
        api.BindingsUpdate.WithTags("Bindings").WithSummary("Update the binding");
        api.BindingsSetBranch.WithTags("Bindings").WithSummary("Move the binding to another branch");
        api.BindingsSetPolling.WithTags("Bindings").WithSummary("Turn the binding's polling on or off");
        api.BindingsReconcile.WithTags("Bindings").WithSummary("Reconcile now");
        api.StepsBlocked.RequireRead().WithTags("Steps").WithSummary("List blocked steps");
        api.StepsBlock.WithTags("Steps").WithSummary("Block a step");
        api.StepsUnblock.WithTags("Steps").WithSummary("Unblock a step");
        api.StepsHistory.RequireRead().WithTags("Steps").WithSummary("Get a step's history");
        api.RunsList.RequireRead().WithTags("Runs").WithSummary("List runs");
        api.RunsStart.WithTags("Runs").WithSummary("Start a run");
        api.RunsCriticalPath.RequireRead().WithTags("Runs").WithSummary("Get the critical path of a range of runs");
        api.RunsGet.RequireRead().WithTags("Runs").WithSummary("Get a run");
        api.RunsCancel.WithTags("Runs").WithSummary("Cancel a run");
        api.RunsRerun.WithTags("Runs").WithSummary("Rerun a run");
        api.RunsContinue.WithTags("Runs").WithSummary("Continue a run");
        api.RunsAttempts.RequireRead().WithTags("Runs").WithSummary("List a run's attempts");
        api.RunStepsAttempts.RequireRead().WithTags("Steps").WithSummary("List a step's attempts in a run");
        api.RunStepsRerun.WithTags("Steps").WithSummary("Rerun a step");
        api.RunStepsCancel.WithTags("Steps").WithSummary("Cancel a step");
        api.RunStepsAnswer.WithTags("Steps").WithSummary("Answer a step's external input");
        api.RunStepsDecline.WithTags("Steps").WithSummary("Decline a step's external input");
        api.RunStepsFiles.WithTags("Steps").WithSummary("List a step's files");
        api.RunStepsOutput.RequireRead().WithTags("Steps").WithSummary("Get a step's output");
        api.RunStepsLogs.RequireRead().WithTags("Steps").WithSummary("Get a step's log");
        api.AttemptsList.RequireRead().WithTags("Attempts").WithSummary("List running attempts");
        api.RunStepsLogStream.RequireRead().WithTags("Steps").WithSummary("Stream a step's log");
        api.EventsStream.RequireRead().WithTags("Events").WithSummary("Stream change events");
        api.ManifestsValidate.WithTags("Versions").WithSummary("Validate a manifest");
    }
}
