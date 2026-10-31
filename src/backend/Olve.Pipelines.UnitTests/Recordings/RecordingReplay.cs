using System.Text.RegularExpressions;
using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.UnitTests.Recordings;

/// <summary>Answers each request with the recording of the same exchange, as the real system did.</summary>
internal static partial class RecordingReplay
{
    public static readonly string Root = RecordingStore.FindRepoRoot(AppContext.BaseDirectory)!;

    public static Recording Get(string system, string name) => RecordingStore.Get(Root, system, name);

    /// <summary>The recording that <paramref name="head"/> asks for, or null when none was captured.</summary>
    public static Recording? Match(IEnumerable<Recording> recordings, string head)
    {
        var target = RequestLine().Match(head).Groups[1].Value;
        var ifNoneMatch = Header(head, "If-None-Match");
        return recordings.FirstOrDefault(r => r.Request.Url == target && r.Request.Headers.GetValueOrDefault("if-none-match") == ifNoneMatch);
    }

    public static string? Header(string head, string name)
    {
        var match = Regex.Match(head, $@"^{name}: (.*?)\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string Respond(Recording recording)
    {
        var headers = recording.Response.Headers.Select(h => $"{h.Key}: {h.Value}\r\n");
        var body = recording.Response.BodyText;
        return $"HTTP/1.1 {recording.Response.Status} X\r\n{string.Concat(headers)}Content-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
    }

    [GeneratedRegex(@"^\w+ (\S+) HTTP/")]
    private static partial Regex RequestLine();
}
