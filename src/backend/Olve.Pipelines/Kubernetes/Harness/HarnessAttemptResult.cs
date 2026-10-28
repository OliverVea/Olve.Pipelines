namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>The verdict of one harness attempt, ready to map onto <c>AttemptService.MarkSucceeded/MarkSkipped/MarkFailed</c>.</summary>
public abstract record HarnessAttemptResult
{
    /// <param name="OutputJson">The step output JSON (stored at <c>output.json</c>), or null for a step without output.</param>
    /// <param name="FileKeys">Garage keys of the re-uploaded <c>files/…</c> objects.</param>
    public sealed record Succeeded(string? OutputJson, IReadOnlyList<string> FileKeys) : HarnessAttemptResult;

    public sealed record Skipped(string Reason) : HarnessAttemptResult;

    public sealed record Failed(HarnessFailureKind Kind, string Message) : HarnessAttemptResult;
}

/// <summary>
/// Combines the pod verdict with what the step reported. The archive envelope is authoritative; the
/// stdout result block is only a fallback for the failure message. Pure.
/// </summary>
public static class HarnessOutcome
{
    /// <param name="pod">The terminal pod/Job state.</param>
    /// <param name="archive">The collected archive: null when no <c>result.tar.gz</c> was uploaded, failed when it was malformed.</param>
    /// <param name="fileKeys">Keys the archive's files were stored under (used for <see cref="HarnessAttemptResult.Succeeded"/>).</param>
    /// <param name="logEnvelopeJson">The stdout result block (<see cref="HarnessSentinels.TryExtractResult"/>), if any.</param>
    public static HarnessAttemptResult Resolve(
        HarnessPodState pod,
        Result<HarnessResultContents>? archive,
        IReadOnlyList<string> fileKeys,
        string? logEnvelopeJson)
    {
        var failure = HarnessExitCodes.Classify(pod);

        // Exit 74: the envelope may say succeeded, but the files never reached storage.
        if (failure is { Kind: HarnessFailureKind.ResultUpload })
            return new HarnessAttemptResult.Failed(failure.Kind, WithDetail(failure.Message, LogFailureMessage(logEnvelopeJson)));

        HarnessEnvelope? envelope = null;
        string? archiveProblem = null;
        if (archive is { } a)
        {
            if (a.TryPickProblems(out var problems, out var contents))
                archiveProblem = string.Join("; ", problems.Select(p => p.FormattedMessage));
            else
                envelope = contents.Envelope;
        }

        if (failure is null)
        {
            return envelope switch
            {
                HarnessEnvelope.Succeeded s => new HarnessAttemptResult.Succeeded(s.OutputOrNull, fileKeys),
                HarnessEnvelope.Skipped s => new HarnessAttemptResult.Skipped(s.Reason),
                HarnessEnvelope.Failed f => new HarnessAttemptResult.Failed(EnvelopeKind(f.Kind), f.Message),
                _ => new HarnessAttemptResult.Failed(HarnessFailureKind.ResultInvalid,
                    archiveProblem ?? "the step exited 0 but uploaded no result.tar.gz"),
            };
        }

        var detail = envelope is HarnessEnvelope.Failed ef ? ef.Message : LogFailureMessage(logEnvelopeJson);
        return new HarnessAttemptResult.Failed(failure.Kind, WithDetail(failure.Message, detail));
    }

    private static HarnessFailureKind EnvelopeKind(string kind) => kind switch
    {
        "input" => HarnessFailureKind.Input,
        "output" => HarnessFailureKind.Output,
        _ => HarnessFailureKind.Handler,
    };

    private static string? LogFailureMessage(string? logEnvelopeJson)
    {
        if (logEnvelopeJson is null) return null;
        return HarnessEnvelope.Parse(logEnvelopeJson).TryPickProblems(out _, out var envelope)
            ? null
            : (envelope as HarnessEnvelope.Failed)?.Message;
    }

    private static string WithDetail(string message, string? detail)
        => string.IsNullOrWhiteSpace(detail) ? message : $"{message}: {detail}";
}
