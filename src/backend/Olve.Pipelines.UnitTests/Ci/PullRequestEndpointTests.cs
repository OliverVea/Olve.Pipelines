using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Olve.Pipelines.Configuration;
using Olve.Pipelines.Pipelines;
using Olve.Pipelines.Pipelines.Ci;
using Olve.Pipelines.Shared;
using Olve.Utilities.Ids;
using static Olve.Pipelines.UnitTests.Ci.PullRequestFixtures;
using Contract = Olve.Pipelines.Api;

namespace Olve.Pipelines.UnitTests.Ci;

public class PullRequestEndpointTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-15T10:00:00Z");

    private static (ServiceProvider Provider, Id<Pipeline> PipelineId) Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create<string, string?>("Ci:Authors:0", PullRequestFixtures.Author)]).Build());
        services.AddPipelineServices();
        var provider = services.BuildServiceProvider();
        foreach (var startup in provider.GetServices<IRunOnStartup>().Where(s => s is PullRequestEventRegistration or PipelineEventRegistration))
            if (startup.Run().TryPickProblems(out var problems))
                throw new InvalidOperationException(string.Join("; ", problems));
        var pipeline = TestRunDomain.Pick(provider.GetRequiredService<PipelineService>().Create("widgets"));
        return (provider, pipeline.Id);
    }

    private static async Task<Contract.PullList> List(ServiceProvider provider, Id<Pipeline> id, string? state = null, int? offset = null, int? limit = null)
    {
        var response = await new ListPullsHandler(provider.GetRequiredService<PipelineService>(), provider.GetRequiredService<PullRequestService>(), provider.GetRequiredService<CiRunViewService>())
            .HandleAsync(new Contract.CiPullsListRequest(id.Value.Value, state, offset, limit), default);
        return ((Contract.CiPullsListResponse.Ok)response).Body;
    }

    [Test]
    public async Task Open_pull_requests_list_newest_first_and_closed_ones_by_when_they_closed()
    {
        var (provider, id) = Build();
        await using var _ = provider;
        var pulls = provider.GetRequiredService<PullRequestService>();
        pulls.Observe(id, "main", Pull(number: 5), T0);
        pulls.Observe(id, "main", Pull(number: 9, draft: true), T0);
        pulls.Observe(id, "main", Pull(number: 7, mergeable: false), T0);
        pulls.Observe(id, "main", Pull(number: 2, state: "closed", merged: true, updatedAt: T0.AddHours(2)), T0);
        pulls.Observe(id, "main", Pull(number: 3, state: "closed", updatedAt: T0.AddHours(1)), T0);

        var open = await List(provider, id);
        var closed = await List(provider, id, "closed");

        await Assert.That(open.Items.Select(p => (p.Number, p.Draft, p.Conflict))).IsEquivalentTo([(9, true, false), (7, false, true), (5, false, false)]);
        await Assert.That(closed.Items.Select(p => (p.Number, p.State))).IsEquivalentTo([(2, Contract.PullState.Merged), (3, Contract.PullState.Closed)]);
    }

    [Test]
    public async Task A_page_of_open_pull_requests_says_how_many_there_are()
    {
        var (provider, id) = Build();
        await using var _ = provider;
        var pulls = provider.GetRequiredService<PullRequestService>();
        foreach (var n in Enumerable.Range(1, 5))
            pulls.Observe(id, "main", Pull(number: n), T0);

        var page = await List(provider, id, offset: 1, limit: 2);

        await Assert.That(page.Items.Select(p => p.Number)).IsEquivalentTo([4, 3]);
        await Assert.That((page.Offset, page.Limit, page.Total)).IsEqualTo((1, 2, 5));
    }

    [Test]
    public async Task An_unknown_pull_request_is_not_found()
    {
        var (provider, id) = Build();
        await using var _ = provider;

        var response = await new GetPullHandler(provider.GetRequiredService<PipelineService>(), provider.GetRequiredService<PullRequestService>(), provider.GetRequiredService<CiRunViewService>())
            .HandleAsync(new Contract.CiPullsGetRequest(id.Value.Value, 41), default);

        await Assert.That(((Contract.CiPullsGetResponse.NotFound)response).Body.Detail).IsEqualTo($"Pull request #41 not found in pipeline '{id}'.");
    }

    [Test]
    public async Task A_pipeline_deleted_takes_its_pull_requests_with_it()
    {
        var (provider, id) = Build();
        await using var _ = provider;
        provider.GetRequiredService<PullRequestService>().Observe(id, "main", Pull(number: 1), T0);

        await Assert.That(provider.GetRequiredService<PipelineService>().Delete(id).Succeeded).IsTrue();

        await Assert.That(provider.GetRequiredService<PullRequestService>().ListByPipeline(id)).IsEmpty();
    }
}
