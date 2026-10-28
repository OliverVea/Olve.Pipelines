namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>
/// Reads the sentinel blocks the harness/olve-build print on stdout (src/sdk/README.md). Everything outside
/// the sentinels is user output and is never parsed. The <b>last</b> complete block wins.
/// <code>
/// ---OLVE-RESULT-BEGIN---\n&lt;envelope, one compact line&gt;\n---OLVE-RESULT-END---\n
/// ---OLVE-MANIFEST-BEGIN---\n&lt;canonical manifest text, ending in \n&gt;---OLVE-MANIFEST-END---\n
/// </code>
/// </summary>
public static class HarnessSentinels
{
    public const string ResultBegin = "---OLVE-RESULT-BEGIN---";
    public const string ResultEnd = "---OLVE-RESULT-END---";
    public const string ManifestBegin = "---OLVE-MANIFEST-BEGIN---";
    public const string ManifestEnd = "---OLVE-MANIFEST-END---";

    /// <summary>The envelope JSON line of the last result block (without its newline), or null.</summary>
    public static string? TryExtractResult(string? log)
    {
        var body = TryExtract(log, ResultBegin, ResultEnd);
        return body is null ? null : body.TrimEnd('\n', '\r');
    }

    /// <summary>
    /// The manifest text of the last manifest block, byte-exact (including its trailing newline) — the
    /// manifest hash is taken over the text as printed. Null when there is no complete block.
    /// </summary>
    public static string? TryExtractManifest(string? log) =>
        TryExtract(log, ManifestBegin, ManifestEnd) is { } manifest ? WithoutBuildLogLines(manifest) : null;

    /// <summary>
    /// The pod log merges olve-build's stderr into its stdout by arrival, so a log line can land inside
    /// the block. A line of the (indented) manifest never starts with a letter.
    /// </summary>
    private static string WithoutBuildLogLines(string manifest)
    {
        if (!manifest.Contains("\nolve-build", StringComparison.Ordinal) && !manifest.StartsWith("olve-build", StringComparison.Ordinal))
            return manifest;
        var lines = manifest.Split('\n').Where(l => !l.StartsWith("olve-build", StringComparison.Ordinal));
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Text between a line that is exactly <paramref name="begin"/> and the next line starting with
    /// <paramref name="end"/>, taking the last such pair. The end marker must start a line.
    /// </summary>
    private static string? TryExtract(string? log, string begin, string end)
    {
        if (string.IsNullOrEmpty(log)) return null;

        var searchFrom = log.Length;
        while (searchFrom > 0)
        {
            var endIdx = log.LastIndexOf(end, searchFrom - 1, StringComparison.Ordinal);
            if (endIdx < 0) return null;
            searchFrom = endIdx;
            if (endIdx > 0 && log[endIdx - 1] != '\n')
                continue; // not at a line start

            var beginIdx = log.LastIndexOf(begin + "\n", endIdx, StringComparison.Ordinal);
            if (beginIdx < 0) return null;
            if (beginIdx > 0 && log[beginIdx - 1] != '\n')
                continue;

            var start = beginIdx + begin.Length + 1;
            // A begin marker after this end would mean an unterminated later block; the found pair is still complete.
            return start <= endIdx ? log[start..endIdx] : null;
        }
        return null;
    }
}
