using System.Text;
using Microsoft.AspNetCore.Http;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.UnitTests.Harness;
using Olve.Pipelines.UnitTests.Scheduling;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Runs;

/// <summary>A step's stored files through the list and download operations, over a real S3 client.</summary>
public class StepFileApiTests
{
    private sealed record Ctx(SchedulerScenario S, FakeS3 S3, Run Run, AttemptFileService Files)
    {
        public Guid PipelineId => S.Pipeline.Id.Value.Value;

        public string Key(int attempt, string objectPath, string step = "checkout") =>
            StorageLayout.AttemptFileKey(S.Pipeline.Id, Run.Number, step, attempt, objectPath);

        public void Store(int attempt, string objectPath, string content) =>
            S3.Objects[Key(attempt, objectPath)] = Encoding.UTF8.GetBytes(content);

        public Task<Contract.RunStepsFilesResponse> List(string step = "checkout", int? attempt = null) =>
            new ListStepFilesHandler(S.D.Query, Files).HandleAsync(new Contract.RunStepsFilesRequest(PipelineId, Run.Number, step, attempt), default);

        public Task<Contract.RunStepsDownloadResponse> Download(string path, string step = "checkout", int? attempt = null) =>
            new DownloadStepFileHandler(S.D.Query, Files, new HttpContextAccessor())
                .HandleAsync(new Contract.RunStepsDownloadRequest(PipelineId, Run.Number, step, path, attempt), default);
    }

    /// <summary>A run whose <c>checkout</c> failed (attempt 1), then succeeded on a rerun (attempt 2).</summary>
    private static Ctx Build()
    {
        var s = new SchedulerScenario();
        var run = s.Start(SchedulerScenario.Input());
        s.Fail(run, "checkout");
        TestRunDomain.Pick(s.D.Continuations.RerunStep(run.Id, "checkout", continueAfter: false));
        s.Succeed(run, "checkout");
        var s3 = new FakeS3();
        return new Ctx(s, s3, run, new AttemptFileService(s3.Client(), new StorageOptions(FakeS3.Bucket)));
    }

    private static async Task<string> Read(Stream stream)
    {
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Test]
    public async Task List_ShowsTheFilesOfTheLatestSuccessfulAttempt_RelativeToIt()
    {
        var c = Build();
        c.Store(1, "old.txt", "old");
        c.Store(2, "a.txt", "a");
        c.Store(2, "out/report.json", "{}");
        c.Store(2, "tree.tar", "tarball");

        var files = ((Contract.RunStepsFilesResponse.Ok)await c.List()).Body;

        await Assert.That(files.Run).IsEqualTo(c.Run.Number);
        await Assert.That(files.Step).IsEqualTo("checkout");
        await Assert.That(files.Attempt).IsEqualTo(2);
        await Assert.That(files.Files.Select(f => (f.Path, f.Size))).IsEquivalentTo(
            [("a.txt", 1L), ("out/report.json", 2L), ("tree.tar", 7L)]);
    }

    [Test]
    public async Task List_OfAnEarlierAttempt_ShowsThatAttempt()
    {
        var c = Build();
        c.Store(1, "old.txt", "old");
        c.Store(2, "a.txt", "a");

        var files = ((Contract.RunStepsFilesResponse.Ok)await c.List(attempt: 1)).Body;

        await Assert.That(files.Attempt).IsEqualTo(1);
        await Assert.That(files.Files.Select(f => f.Path)).IsEquivalentTo(["old.txt"]);
    }

    [Test]
    public async Task List_FollowsPagesToTheEnd()
    {
        var c = Build();
        c.S3.ListPageSize = 2;
        var names = Enumerable.Range(0, 5).Select(i => $"f{i}.txt").ToList();
        foreach (var name in names)
            c.Store(2, name, "x");

        var files = ((Contract.RunStepsFilesResponse.Ok)await c.List()).Body;

        await Assert.That(files.Files.Select(f => f.Path)).IsEquivalentTo(names);
    }

    [Test]
    [Arguments("out/report.json", "out/report.json", "report.json")]
    [Arguments("chart/", "chart.tar", "chart.tar")]
    public async Task Download_ServesTheStoredObject(string path, string objectPath, string fileName)
    {
        var c = Build();
        c.Store(2, objectPath, "content");

        var file = (Contract.RunStepsDownloadResponse.Ok)await c.Download(path);

        await Assert.That(await Read(file.Body)).IsEqualTo("content");
        await Assert.That(file.ContentLength).IsEqualTo(7L);
        await Assert.That(file.ContentDisposition).IsEqualTo($"attachment; filename={fileName}; filename*=UTF-8''{fileName}");
    }

    [Test]
    public async Task Download_OfAnEarlierAttempt_ServesThatAttempt()
    {
        var c = Build();
        c.Store(1, "a.txt", "first");
        c.Store(2, "a.txt", "second");

        var file = (Contract.RunStepsDownloadResponse.Ok)await c.Download("a.txt", attempt: 1);

        await Assert.That(await Read(file.Body)).IsEqualTo("first");
    }

    [Test]
    [Arguments(null, "application/octet-stream")]
    [Arguments("binary/octet-stream", "application/octet-stream")]
    [Arguments("application/json", "application/json")]
    public async Task Download_ReportsAContentType(string? stored, string expected)
    {
        var c = Build();
        c.Store(2, "a.txt", "x");
        if (stored is not null)
            c.S3.ContentTypes[c.Key(2, "a.txt")] = stored;

        var file = (Contract.RunStepsDownloadResponse.Ok)await c.Download("a.txt");

        await Assert.That(file.ContentType).IsEqualTo(expected);
    }

    [Test]
    public async Task Attachment_KeepsANonAsciiFileName()
        => await Assert.That(DownloadStepFileHandler.Attachment("rapport é.txt")).Contains("filename*=UTF-8''rapport%20%C3%A9.txt");

    [Test]
    public async Task Download_OfAMissingFile_IsNotFound()
    {
        var c = Build();

        var problem = ((Contract.RunStepsDownloadResponse.NotFound)await c.Download("chart/")).Body;

        await Assert.That(problem.Detail).IsEqualTo($"Attempt 2 of step 'checkout' in run #{c.Run.Number} has no file 'chart.tar'.");
    }

    [Test]
    [Arguments(null, "Run #1 has no successful attempt of step 'build'.")]
    [Arguments(9, "Run #1 has no attempt 9 of step 'build'.")]
    public async Task AnAttemptThatDoesNotExist_IsNotFound(int? attempt, string expected)
    {
        var c = Build();

        await Assert.That(((Contract.RunStepsFilesResponse.NotFound)await c.List("build", attempt)).Body.Detail).IsEqualTo(expected);
        await Assert.That(((Contract.RunStepsDownloadResponse.NotFound)await c.Download("a.txt", "build", attempt)).Body.Detail).IsEqualTo(expected);
    }

    [Test]
    [Arguments("../other/secret.txt", "File path '../other/secret.txt' has an empty, '.' or '..' segment.")]
    [Arguments("/etc/passwd", "File path '/etc/passwd' must be relative, without '\\' or NUL.")]
    public async Task Download_RefusesAPathOutsideTheAttempt(string path, string expected)
    {
        var c = Build();

        var problem = ((Contract.RunStepsDownloadResponse.BadRequest)await c.Download(path)).Body;

        await Assert.That(problem.Status).IsEqualTo(400);
        await Assert.That(problem.Errors.Single().Message).IsEqualTo(expected);
        await Assert.That(problem.Errors.Single().Field).IsEqualTo("path");
    }

    [Test]
    public async Task GetSize_IsTheStoredSize_OrNullWhenMissing()
    {
        var c = Build();
        c.Store(2, "a.txt", "four");
        var attempt = c.S.Latest(c.Run, "checkout")!;

        await Assert.That(await c.Files.GetSizeAsync(attempt, "a.txt", default)).IsEqualTo(4L);
        await Assert.That(await c.Files.GetSizeAsync(attempt, "gone.txt", default)).IsNull();
    }
}
