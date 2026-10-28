using System.Text.Json.Serialization;
using Olve.Pipelines.Cli.Api.Contracts;

namespace Olve.Pipelines.Cli.Api;

/// <summary>
/// Source-generated <see cref="JsonSerializerContext"/> covering every DTO the CLI (de)serializes.
/// This is what keeps <c>pl</c> Native-AOT: all JSON goes through generated <c>JsonTypeInfo</c>,
/// never reflection. A missing <c>[JsonSerializable]</c> surfaces as a runtime
/// <c>NotSupportedException</c>, so each new command registers the types it reads/writes here.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
// Local CLI config (~/.pl)
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(CliConfigFile))]
[JsonSerializable(typeof(Olve.Pipelines.Cli.Commands.Profiles.ProfileInfo[]))]
[JsonSerializable(typeof(AuthConfig))]
// OIDC login flow (pl login)
[JsonSerializable(typeof(FrontendOidcConfig))]
[JsonSerializable(typeof(OidcDiscovery))]
[JsonSerializable(typeof(DeviceAuthResponse))]
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(LoginResult))]
// Pipelines
[JsonSerializable(typeof(Pipeline))]
[JsonSerializable(typeof(Pipeline[]))]
// Attempts
[JsonSerializable(typeof(Attempt))]
[JsonSerializable(typeof(AttemptStatus))]
[JsonSerializable(typeof(ActiveAttempt[]))]
// Bindings (GitOps)
[JsonSerializable(typeof(PipelineConfigBinding))]
[JsonSerializable(typeof(PipelineBindingStatus))]
// Mutation request bodies
[JsonSerializable(typeof(SetSecretRequest))]
[JsonSerializable(typeof(CopySecretsRequest))]
[JsonSerializable(typeof(CopySecretsResponse))]
[JsonSerializable(typeof(PipelineSecretInfo[]))]
[JsonSerializable(typeof(CreatePipelineWithRepoRequest))]
[JsonSerializable(typeof(UpdateBindingRequest))]
[JsonSerializable(typeof(SetBindingBranchRequest))]
// Secrets, blocked steps, logs (a JSON string)
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
// Runs (v2)
[JsonSerializable(typeof(Attempt[]))]
[JsonSerializable(typeof(RunList))]
[JsonSerializable(typeof(CriticalPathReport))]
[JsonSerializable(typeof(Run))]
[JsonSerializable(typeof(RunStart))]
[JsonSerializable(typeof(AttemptOutputResponse))]
[JsonSerializable(typeof(AttemptFilesResponse))]
[JsonSerializable(typeof(StepHistoryItem[]))]
[JsonSerializable(typeof(RerunStepRequest))]
[JsonSerializable(typeof(SetBindingPollingRequest))]
[JsonSerializable(typeof(AnswerInputRequest))]
[JsonSerializable(typeof(DeclineInputRequest))]
[JsonSerializable(typeof(ServerProblem[]))]
[JsonSerializable(typeof(ServerProblemBody))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
// Versions, health, manifests
[JsonSerializable(typeof(PipelineVersionsResponse))]
[JsonSerializable(typeof(PipelineVersion))]
[JsonSerializable(typeof(PipelineActivation))]
[JsonSerializable(typeof(SwitchVersionRequest))]
[JsonSerializable(typeof(PipelineHealth))]
[JsonSerializable(typeof(ManifestValidationResponse))]
// Config
[JsonSerializable(typeof(ConfigDocument))]
[JsonSerializable(typeof(ConfigSaveResponse))]
[JsonSerializable(typeof(ConfigSnapshotInfo[]))]
[JsonSerializable(typeof(ConfigCheckResponse))]
[JsonSerializable(typeof(EffectivePipelineResponse))]
[JsonSerializable(typeof(SetConfigRequest))]
[JsonSerializable(typeof(RollbackConfigRequest))]
public sealed partial class CliJsonContext : JsonSerializerContext;
