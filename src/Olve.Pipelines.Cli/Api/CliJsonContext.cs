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
[JsonSerializable(typeof(PipelineSummary))]
[JsonSerializable(typeof(PipelineSummary[]))]
// Jobs
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(PageOfJob))]
[JsonSerializable(typeof(JobStatus))]
// Bindings (GitOps)
[JsonSerializable(typeof(PipelineConfigBinding))]
[JsonSerializable(typeof(PipelineBindingStatus))]
// Mutation request bodies
[JsonSerializable(typeof(SetSecretRequest))]
[JsonSerializable(typeof(CreatePipelineWithRepoRequest))]
[JsonSerializable(typeof(UpdateBindingRequest))]
[JsonSerializable(typeof(SetDeployTriggerRequest))]
// Secrets
[JsonSerializable(typeof(string[]))]
public sealed partial class CliJsonContext : JsonSerializerContext;
