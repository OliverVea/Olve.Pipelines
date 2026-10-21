using Olve.Results;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Harness;

public class HarnessResultArchiveTests
{
    private const string SucceededEnvelope = """{"output":{"tree":{"run":1,"step":"build","attempt":1,"path":"chart/","sha256":"00"}},"status":"succeeded"}""";

    private abstract record Entry(string Name);
    private sealed record FileEntry(string Name, byte[] Data) : Entry(Name);
    private sealed record DirEntry(string Name) : Entry(Name);
    private sealed record LinkEntry(string Name, string Target, TarEntryType Type) : Entry(Name);

    private static FileEntry F(string name, string text) => new(name, Encoding.UTF8.GetBytes(text));

    private static MemoryStream Archive(params Entry[] entries)
    {
        var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var e in entries)
            {
                switch (e)
                {
                    case FileEntry f:
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, f.Name) { DataStream = new MemoryStream(f.Data) });
                        break;
                    case DirEntry d:
                        tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, d.Name));
                        break;
                    case LinkEntry l:
                        tar.WriteEntry(new PaxTarEntry(l.Type, l.Name) { LinkName = l.Target });
                        break;
                }
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static async Task<(Result<HarnessResultContents> Result, Dictionary<string, string> Sunk)> Read(Stream archive, HarnessResultLimits? limits = null)
    {
        var sunk = new Dictionary<string, string>();
        var result = await HarnessResultArchive.ReadAsync(archive, limits ?? new HarnessResultLimits(), async (path, content, _, ct) =>
        {
            using var reader = new StreamReader(content);
            sunk[path] = await reader.ReadToEndAsync(ct);
        });
        return (result, sunk);
    }

    [Test]
    public async Task ValidArchive_YieldsEnvelopeAndFiles()
    {
        var (result, sunk) = await Read(Archive(
            F("output.json", SucceededEnvelope),
            new DirEntry("files/"),
            F("files/chart.tar", "tar-bytes"),
            new DirEntry("files/sub/"),
            F("files/sub/a.txt", "hello")));

        await Assert.That(result).Succeeded();
        await Assert.That(result.Value!.Envelope).IsTypeOf<HarnessEnvelope.Succeeded>();
        await Assert.That(result.Value.Files).IsEquivalentTo(new[] { "chart.tar", "sub/a.txt" });
        await Assert.That(sunk["sub/a.txt"]).IsEqualTo("hello");
        await Assert.That(sunk["chart.tar"]).IsEqualTo("tar-bytes");
    }

    [Test]
    public async Task DotSlashPrefix_IsAccepted()
    {
        var (result, sunk) = await Read(Archive(F("./output.json", """{"reason":"r","status":"skipped"}"""), F("./files/x", "1")));

        await Assert.That(result).Succeeded();
        await Assert.That(sunk.Keys).Contains("x");
    }

    [Test]
    [Arguments("files/../../etc/passwd")]
    [Arguments("../output.json")]
    [Arguments("/etc/passwd")]
    [Arguments("files//x")]
    [Arguments("files/./x")]
    [Arguments("files/a\\..\\b")]
    [Arguments("other.txt")]
    [Arguments("files")]
    public async Task MaliciousPaths_AreRejected(string name)
    {
        var (result, sunk) = await Read(Archive(F("output.json", SucceededEnvelope), F(name, "evil")));

        await Assert.That(result).Failed();
        await Assert.That(sunk.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(TarEntryType.SymbolicLink)]
    [Arguments(TarEntryType.HardLink)]
    public async Task Links_AreRejected(TarEntryType type)
    {
        var (result, _) = await Read(Archive(F("output.json", SucceededEnvelope), new LinkEntry("files/link", "/etc/passwd", type)));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task DirectoryOutsideFiles_IsRejected()
    {
        var (result, _) = await Read(Archive(F("output.json", SucceededEnvelope), new DirEntry("files/../x/")));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Duplicates_AreRejected()
    {
        var (result, _) = await Read(Archive(F("output.json", SucceededEnvelope), F("files/a", "1"), F("files/a", "2")));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task MissingOutputJson_IsRejected()
    {
        var (result, _) = await Read(Archive(F("files/a", "1")));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task MalformedEnvelope_IsRejected()
    {
        var (result, _) = await Read(Archive(F("output.json", """{"status":"succeeded"}""")));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Oversize_TotalBytes_IsRejected()
    {
        var limits = new HarnessResultLimits { MaxTotalBytes = 1024 };
        var (result, _) = await Read(Archive(F("output.json", SucceededEnvelope), new FileEntry("files/big", new byte[4096])), limits);

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task Oversize_Envelope_IsRejected()
    {
        var limits = new HarnessResultLimits { MaxEnvelopeBytes = 16 };
        var (result, _) = await Read(Archive(F("output.json", SucceededEnvelope)), limits);

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task TooManyEntries_IsRejected()
    {
        var limits = new HarnessResultLimits { MaxEntries = 3 };
        var (result, _) = await Read(Archive(F("output.json", SucceededEnvelope), F("files/a", "1"), F("files/b", "1"), F("files/c", "1")), limits);

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task NotGzip_IsRejected()
    {
        var (result, _) = await Read(new MemoryStream(Encoding.UTF8.GetBytes("definitely not a tarball")));

        await Assert.That(result).Failed();
    }

    [Test]
    public async Task TruncatedArchive_IsRejected()
    {
        var full = Archive(F("output.json", SucceededEnvelope), new FileEntry("files/a", new byte[8192])).ToArray();
        var (result, _) = await Read(new MemoryStream(full[..(full.Length / 2)]));

        await Assert.That(result).Failed();
    }
}
