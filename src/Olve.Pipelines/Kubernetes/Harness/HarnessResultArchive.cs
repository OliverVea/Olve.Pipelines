using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>The parsed contents of a <c>result.tar.gz</c>: the envelope and the object paths under <c>files/</c>.</summary>
public sealed record HarnessResultContents(HarnessEnvelope Envelope, string EnvelopeJson, IReadOnlyList<string> Files);

/// <summary>
/// Reads a harness <c>result.tar.gz</c> (<c>output.json</c> + <c>files/…</c>, sdk/README.md "Result
/// upload"). Untrusted input: only regular files named <c>output.json</c> or <c>files/&lt;safe path&gt;</c>
/// are accepted (directories skipped); links, devices, absolute or <c>..</c> paths, backslashes, NULs
/// and duplicates are rejected; entry count, envelope and total uncompressed size are capped.
/// <para>
/// Each file is handed to <c>fileSink(objectPath, content, length)</c> as it streams past; the sink must
/// consume the stream before returning. On failure, files already sunk stay sunk — the caller decides.
/// </para>
/// </summary>
public static class HarnessResultArchive
{
    public delegate Task FileSink(string objectPath, Stream content, long length, CancellationToken ct);

    public static async Task<Result<HarnessResultContents>> ReadAsync(
        Stream gzipped, HarnessResultLimits limits, FileSink fileSink, CancellationToken ct = default)
    {
        try
        {
            await using var gzip = new GZipStream(gzipped, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip, leaveOpen: true);

            string? envelopeJson = null;
            var files = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            var entries = 0;

            while (await reader.GetNextEntryAsync(copyData: false, ct) is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
                    continue;
                if (++entries > limits.MaxEntries)
                    return Fail("more than {0} entries", limits.MaxEntries);

                var name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name;

                if (entry.EntryType is TarEntryType.Directory)
                {
                    var dir = name.TrimEnd('/');
                    if (dir != "files" && !(dir.StartsWith("files/", StringComparison.Ordinal) && IsSafeRelative(dir["files/".Length..])))
                        return Fail("unexpected directory '{0}'", Printable(name));
                    continue;
                }

                if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    return Fail("entry '{0}' has unsupported type {1}", Printable(name), entry.EntryType);

                if (!seen.Add(name))
                    return Fail("duplicate entry '{0}'", Printable(name));

                total += entry.Length;
                if (entry.Length < 0 || total > limits.MaxTotalBytes)
                    return Fail("contents exceed {0} bytes", limits.MaxTotalBytes);

                var data = entry.DataStream ?? Stream.Null;

                if (name == "output.json")
                {
                    if (entry.Length > limits.MaxEnvelopeBytes)
                        return Fail("output.json exceeds {0} bytes", limits.MaxEnvelopeBytes);
                    using var buffer = new MemoryStream();
                    await data.CopyToAsync(buffer, ct);
                    envelopeJson = Encoding.UTF8.GetString(buffer.ToArray());
                    continue;
                }

                if (!name.StartsWith("files/", StringComparison.Ordinal) || !IsSafeRelative(name["files/".Length..]))
                    return Fail("unexpected entry '{0}'", Printable(name));

                var objectPath = name["files/".Length..];
                await fileSink(objectPath, new LengthLimitedStream(data, entry.Length), entry.Length, ct);
                files.Add(objectPath);
            }

            if (envelopeJson is null)
                return Fail("no output.json");

            var envelope = HarnessEnvelope.Parse(envelopeJson, limits.MaxEnvelopeBytes);
            if (envelope.TryPickProblems(out var problems, out var parsed))
                return Result.Failure<HarnessResultContents>(problems);

            return new HarnessResultContents(parsed, envelopeJson, files);
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or EndOfStreamException)
        {
            return Fail("not a valid tar.gz: {0}", e.Message);
        }
    }

    /// <summary>A relative path of non-empty segments, none <c>.</c>/<c>..</c>, without <c>\</c>, NUL or control characters.</summary>
    public static bool IsSafeRelative(string path)
    {
        if (path.Length == 0 || path.Length > 1024 || path.StartsWith('/') || path.EndsWith('/'))
            return false;
        if (path.Any(c => c == '\\' || char.IsControl(c)))
            return false;
        return path.Split('/').All(s => s.Length > 0 && s != "." && s != "..");
    }

    private static string Printable(string name)
    {
        var cleaned = new string(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return cleaned.Length > 200 ? cleaned[..200] + "…" : cleaned;
    }

    private static Result<HarnessResultContents> Fail(string format, params object[] args)
        => Result.Failure<HarnessResultContents>(new ResultProblem("Invalid result archive: " + format, args));

    /// <summary>Read-only view that refuses to yield more than the declared entry length.</summary>
    private sealed class LengthLimitedStream(Stream inner, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= n;
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining <= 0) return 0;
            var n = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct);
            _remaining -= n;
            return n;
        }
    }
}
