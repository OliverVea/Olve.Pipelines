using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Pipelines.Versions;

namespace Olve.Pipelines.Pipelines;

/// <summary>
/// The single source of every Garage object key the v2 domain derives (spec §5.5). Keys mirror the
/// addressing scheme <c>&lt;pipeline&gt;#&lt;run&gt;/&lt;step&gt;[@&lt;attempt&gt;]</c>, using the pipeline id in
/// <c>N</c> format, the per-pipeline run number and the per-(run, step) attempt number:
/// <code>
/// versions/&lt;p&gt;/&lt;n&gt;/step.tar.gz        compiled harness binary of pipeline version n
/// versions/&lt;p&gt;/&lt;n&gt;/manifest.json      canonical manifest of version n (write-once)
/// runs/&lt;p&gt;/&lt;run&gt;/run.json            persisted state of one run and its attempts
/// runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;a&gt;/output.json
/// runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;a&gt;/files/…
/// runs/&lt;p&gt;/&lt;run&gt;/&lt;step&gt;/&lt;a&gt;/log
/// </code>
/// </summary>
public static class StorageLayout
{
    public static string VersionPrefix(Id<Pipeline> pipelineId, int versionNumber)
        => $"versions/{Format(pipelineId)}/{versionNumber}";

    public static string VersionBinaryKey(Id<Pipeline> pipelineId, int versionNumber)
        => $"{VersionPrefix(pipelineId, versionNumber)}/step.tar.gz";

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

    public static string AttemptLogKey(Attempt attempt) => $"{AttemptPrefix(attempt)}/log";

    private static string Format(Id<Pipeline> pipelineId) => pipelineId.Value.Value.ToString("N");
}
