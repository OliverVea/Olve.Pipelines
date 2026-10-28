using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// The single source of every Garage object key the v2 domain derives (spec §5.5). Keys mirror the
/// addressing scheme <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;[@&lt;attempt&gt;]</c>, using the pipeline id in
/// <c>N</c> format, the per-pipeline run number and the per-(run, step) attempt number:
/// <code>
/// versions/&lt;p&gt;/builds/&lt;sha&gt;/step.tar.gz compiled harness binary built for a commit (a version keeps its key)
/// versions/&lt;p&gt;/&lt;n&gt;/step.tar.gz        (legacy default binary key of version n)
/// versions/&lt;p&gt;/&lt;n&gt;/manifest.json      canonical manifest of version n (write-once)
/// runs/&lt;p&gt;/&lt;run&gt;/run.json            persisted state of one run and its attempts
/// runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;a&gt;/output.json
/// runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;a&gt;/files/…
/// runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;a&gt;/log
/// runs/&lt;p&gt;/&lt;run&gt;/_failure/&lt;handler&gt;/&lt;id&gt;/{input.json,log}   failure-handler runs
/// config/&lt;p&gt;/&lt;n&gt;.yaml                config snapshot n, as written (write-once)
/// </code>
/// </summary>
public static class StorageLayout
{
    public static string VersionPrefix(Id<Pipeline> pipelineId, int versionNumber)
        => $"versions/{Format(pipelineId)}/{versionNumber}";

    public static string VersionBinaryKey(Id<Pipeline> pipelineId, int versionNumber)
        => $"{VersionPrefix(pipelineId, versionNumber)}/step.tar.gz";

    /// <summary>
    /// Where reconcile's build/describe Job PUTs the compiled harness for a commit, before a version
    /// number exists; a version added from that build keeps this as its <see cref="PipelineVersion.BinaryKey"/>.
    /// Keyed by commit so a restarted reconcile finds (and reuses) the same build.
    /// </summary>
    public static string BuildBinaryKey(Id<Pipeline> pipelineId, string commitSha)
        => $"versions/{Format(pipelineId)}/builds/{commitSha}/step.tar.gz";

    public static string VersionManifestKey(Id<Pipeline> pipelineId, int versionNumber)
        => $"{VersionPrefix(pipelineId, versionNumber)}/manifest.json";

    public static string VersionManifestKey(PipelineVersion version)
        => VersionManifestKey(version.PipelineId, version.Number);

    public static string RunPrefix(Id<Pipeline> pipelineId, int runNumber)
        => $"runs/{Format(pipelineId)}/{runNumber}";

    public static string RunStateKey(Id<Pipeline> pipelineId, int runNumber)
        => $"{RunPrefix(pipelineId, runNumber)}/run.json";

    public static string AttemptPrefix(Id<Pipeline> pipelineId, int runNumber, string stepName, int attemptNumber)
        => $"{RunPrefix(pipelineId, runNumber)}/{stepName}/{attemptNumber}";

    public static string AttemptPrefix(Attempt attempt)
        => AttemptPrefix(attempt.PipelineId, attempt.RunNumber, attempt.StepName, attempt.Number);

    public static string AttemptOutputKey(Attempt attempt) => $"{AttemptPrefix(attempt)}/output.json";

    public static string AttemptFilesPrefix(Attempt attempt) => $"{AttemptPrefix(attempt)}/files";

    public static string AttemptFilesPrefix(Id<Pipeline> pipelineId, int runNumber, string stepName, int attemptNumber)
        => $"{AttemptPrefix(pipelineId, runNumber, stepName, attemptNumber)}/files";

    /// <summary>Garage key of one stored file object (<paramref name="objectPath"/>: a file ref's path, or
    /// <c>dir.tar</c> for a directory ref <c>dir/</c> — see <c>src/sdk/README.md</c> "Storage rule").</summary>
    public static string AttemptFileKey(Id<Pipeline> pipelineId, int runNumber, string stepName, int attemptNumber, string objectPath)
        => $"{AttemptFilesPrefix(pipelineId, runNumber, stepName, attemptNumber)}/{objectPath}";

    public static string AttemptLogKey(Attempt attempt) => $"{AttemptPrefix(attempt)}/log";

    /// <summary>
    /// Objects of one failure-handler run (<c>input.json</c>, <c>log</c>), under the failed run so retention
    /// removes them with it. <c>_failure</c> cannot collide with a step directory: step names are DNS labels.
    /// </summary>
    public static string FailureHandlerPrefix(Id<Pipeline> pipelineId, int runNumber, string handlerName, string dispatchId)
        => $"{RunPrefix(pipelineId, runNumber)}/_failure/{handlerName}/{dispatchId}";

    public static string ConfigSnapshotKey(Id<Pipeline> pipelineId, int snapshotNumber)
        => $"config/{Format(pipelineId)}/{snapshotNumber}.yaml";

    private static string Format(Id<Pipeline> pipelineId) => pipelineId.Value.Value.ToString("N");
}
