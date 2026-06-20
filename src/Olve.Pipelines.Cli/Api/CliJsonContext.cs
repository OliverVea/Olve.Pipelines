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
[JsonSerializable(typeof(TokenResponse))]
[JsonSerializable(typeof(LoginResult))]
// Pipelines
[JsonSerializable(typeof(Pipeline))]
[JsonSerializable(typeof(Pipeline[]))]
[JsonSerializable(typeof(PipelineSummary))]
[JsonSerializable(typeof(PipelineSummary[]))]
// Steps
[JsonSerializable(typeof(ProductionStep))]
[JsonSerializable(typeof(ProductionStep[]))]
[JsonSerializable(typeof(ProcessingStep))]
[JsonSerializable(typeof(ProcessingStep[]))]
[JsonSerializable(typeof(StepConfiguration))]
[JsonSerializable(typeof(PromotionState))]
[JsonSerializable(typeof(StepPromotionState))]
[JsonSerializable(typeof(StepPromotionState[]))]
// Jobs (polymorphic)
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(PageOfJob))]
[JsonSerializable(typeof(JobStatus))]
// Job groups (polymorphic) — returned by trigger/re-promote mutations
[JsonSerializable(typeof(JobGroup))]
// Mutation request bodies
[JsonSerializable(typeof(SetPromotionRequest))]
// Triggers (polymorphic)
[JsonSerializable(typeof(Trigger))]
[JsonSerializable(typeof(Trigger[]))]
[JsonSerializable(typeof(TriggerTarget))]
// Artifact bundles
[JsonSerializable(typeof(ArtifactBundle))]
[JsonSerializable(typeof(ArtifactBundle[]))]
// Secrets
[JsonSerializable(typeof(string[]))]
public sealed partial class CliJsonContext : JsonSerializerContext;
