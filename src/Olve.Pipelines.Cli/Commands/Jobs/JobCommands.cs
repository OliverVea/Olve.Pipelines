using Olve.Pipelines.Cli.Api;
using Olve.Pipelines.Cli.Api.Contracts;
using Olve.Pipelines.Cli.Output;

namespace Olve.Pipelines.Cli.Commands.Jobs;

/// <summary><c>pl job list [--pipeline &lt;id&gt;] [--page N] [--page-size N] [--sort created-asc|created-desc]</c>.</summary>
public sealed class JobListCommand : ICliCommand
{
    public string Noun => "job";
    public string Verb => "list";
    public string HelpLine => "List jobs (paginated; filter by --pipeline)";
    public string? HelpDetail =>
        """
        pl job list [options]   List jobs (latest first by default)

          --pipeline <id>   Only jobs for this pipeline
          --page <n>        Page number (default: 0)
          --page-size <n>   Page size (default: 50)
          --sort <order>    created-desc (default) | created-asc
        """;

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if (!int.TryParse(cli.Option("page", "0"), out var page))
            return new ResultProblem("--page must be an integer.");
        if (!int.TryParse(cli.Option("page-size", "50"), out var pageSize))
            return new ResultProblem("--page-size must be an integer.");

        var sortText = cli.Option("sort", "created-desc");
        var sort = sortText switch
        {
            "created-desc" => 0,
            "created-asc" => 1,
            _ => -1,
        };
        if (sort < 0)
            return new ResultProblem("--sort must be 'created-desc' or 'created-asc'.");

        var pipelineId = cli.Option("pipeline");
        if ((await ctx.Api.ListJobs(pipelineId, page, pageSize, sort, ct)).TryPickProblems(out var problems, out var jobsPage))
            return problems;

        // A job carries only its step id, so resolve ids to names for the human table by fetching each
        // pipeline's step list once (the JSON output keeps the raw step ids on the job objects).
        var stepNames = new Dictionary<Guid, string>();
        if (!ctx.Output.IsJson)
        {
            foreach (var pid in jobsPage.Items.Select(j => j.PipelineId).Distinct())
            {
                if (!(await ctx.Api.ListProductionSteps(pid.ToString(), ct)).TryPickProblems(out _, out var prod))
                    foreach (var s in prod)
                        stepNames[s.Id] = s.Name;
                if (!(await ctx.Api.ListProcessingSteps(pid.ToString(), ct)).TryPickProblems(out _, out var proc))
                    foreach (var s in proc)
                        stepNames[s.Id] = s.Name;
            }
        }

        ctx.Output.Emit(jobsPage, CliJsonContext.Default.PageOfJob, () =>
        {
            var rows = jobsPage.Items
                .Select(j => (IReadOnlyList<string>)
                    [j.Id.ToString(), j.Kind, JobCommandHelpers.StepLabel(j, stepNames), j.Status.Kind, JobCommandHelpers.Format(j.CreatedAt)])
                .ToList();
            foreach (var line in Table.Render(["ID", "TYPE", "STEP", "STATUS", "CREATED"], rows))
                ctx.Output.Line(line);
            ctx.Output.Line();
            ctx.Output.Line($"page {jobsPage.PageNumber} · {jobsPage.Items.Length} of {jobsPage.TotalCount} job(s)");
        });
        return Result.Success();
    }
}

/// <summary><c>pl job get &lt;id&gt;</c> — show a single job.</summary>
public sealed class JobGetCommand : ICliCommand
{
    public string Noun => "job";
    public string Verb => "get";
    public int RequiredOperands => 1;
    public string HelpLine => "Get a job by id";
    public string? HelpDetail => "pl job get <jobId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetJob(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var job))
            return problems;

        ctx.Output.Emit(job, CliJsonContext.Default.Job, () =>
        {
            ctx.Output.Line($"Id:       {job.Id}");
            ctx.Output.Line($"Type:     {job.Kind}");
            ctx.Output.Line($"Status:   {job.Status.Kind}");
            ctx.Output.Line($"Pipeline: {job.PipelineId}");
            ctx.Output.Line($"Group:    {job.JobGroupId}");
            ctx.Output.Line($"Created:  {JobCommandHelpers.Format(job.CreatedAt)}");
            switch (job)
            {
                case ProductionJob p:
                    ctx.Output.Line($"Step:     {p.ProductionStepId} (production)");
                    break;
                case ProcessingJob p:
                    ctx.Output.Line($"Step:     {p.ProcessingStepId} (processing)");
                    ctx.Output.Line($"Bundle:   {p.ArtifactBundleId}");
                    break;
            }
        });
        return Result.Success();
    }
}

/// <summary><c>pl job logs &lt;id&gt;</c> — print a job's logs (available once the job completes).</summary>
public sealed class JobLogsCommand : ICliCommand
{
    public string Noun => "job";
    public string Verb => "logs";
    public int RequiredOperands => 1;
    public string HelpLine => "Print a job's logs";
    public string? HelpDetail => "pl job logs <jobId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetJobLogs(cli.Operand(0)!, ct)).TryPickProblems(out var problems, out var logs))
            return problems;

        ctx.Output.Line(logs);
        return Result.Success();
    }
}

/// <summary><c>pl job queue</c> — the queued job ids, in order.</summary>
public sealed class JobQueueCommand : ICliCommand
{
    public string Noun => "job";
    public string Verb => "queue";
    public string HelpLine => "Show the job queue (queued job ids)";
    public string? HelpDetail => "pl job queue";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        if ((await ctx.Api.GetJobQueueRaw(ct)).TryPickProblems(out var problems, out var queue))
            return problems;

        ctx.Output.Line(queue);
        return Result.Success();
    }
}

/// <summary><c>pl job cancel &lt;id&gt;</c> — cancel a scheduled or in-progress job.</summary>
public sealed class JobCancelCommand : ICliCommand
{
    public string Noun => "job";
    public string Verb => "cancel";
    public int RequiredOperands => 1;
    public string HelpLine => "Cancel a scheduled or in-progress job";
    public string? HelpDetail => "pl job cancel <jobId>";

    public async Task<Result> Execute(CliArgs cli, CommandContext ctx, CancellationToken ct)
    {
        var id = cli.Operand(0)!;
        if ((await ctx.Api.CancelJob(id, ct)).TryPickProblems(out var problems))
            return problems;

        if (!ctx.Output.IsJson)
            ctx.Output.Line($"Cancelled job {id}.");
        return Result.Success();
    }
}

internal static class JobCommandHelpers
{
    public static string Format(DateTimeOffset value) => value.ToString("u");

    /// <summary>The step a job ran for: its name when resolvable, else the bare step id, else "-".</summary>
    public static string StepLabel(Job job, IReadOnlyDictionary<Guid, string> stepNames)
    {
        var stepId = job switch
        {
            ProductionJob p => p.ProductionStepId,
            ProcessingJob pr => pr.ProcessingStepId,
            _ => (Guid?)null,
        };
        if (stepId is not { } id)
            return "-";
        return stepNames.TryGetValue(id, out var name) ? name : id.ToString();
    }
}
