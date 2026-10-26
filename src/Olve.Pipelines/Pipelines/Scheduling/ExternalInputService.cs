using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>
/// Answers or declines a step that waits for its external inputs. The value is validated against the input's
/// model in the run's pinned manifest; the last missing answer schedules the step. Transient; under the
/// scheduling lock.
/// </summary>
public class ExternalInputService(
    RunService runs,
    AttemptService attempts,
    RunPlanCache plans,
    StageScheduler scheduler,
    RunSchedulingLock schedulingLock,
    TimeProvider timeProvider,
    ILogger<ExternalInputService> logger)
{
    /// <param name="name">The external input; may be omitted when the step has exactly one.</param>
    /// <param name="answeredBy">Who answered, for the record (a user's name).</param>
    public Result<Attempt> Answer(Id<Pipeline> pipelineId, int runNumber, string stepName, string? name, JsonElement value, string answeredBy)
        => schedulingLock.Run<Result<Attempt>>(() =>
        {
            if (Find(pipelineId, runNumber, stepName).TryPickProblems(out var problems, out var found))
                return problems;
            var (run, step, attempt, manifest) = found;

            if (PickInput(step, name).TryPickProblems(out var nameProblems, out var input))
                return nameProblems;
            var (inputName, external) = input;
            if (!manifest.Models.TryGetValue(external.Model, out var schemas))
                return new ResultProblem("Model '{0}' of external input '{1}' is not in the manifest.", external.Model, inputName);

            if (JsonSchemaValidator.Validate(schemas.Input ?? schemas.Output, JsonNode.Parse(value.GetRawText()), "value")
                .TryPickProblems(out var invalid, out var validated))
                return invalid;

            var answer = new ExternalAnswer(validated?.ToJsonString() ?? "null", answeredBy, timeProvider.GetUtcNow());
            if (attempts.Answer(attempt.Id, inputName, answer, step.ExternalInputs.Keys.ToList()).TryPickProblems(out var answerProblems, out var answered))
                return answerProblems;

            logger.LogInformation("Answered input '{InputName}' of step '{StepName}' in run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' ({AnsweredBy}); the step is {Status}",
                inputName, stepName, run.Number, run.Id, run.PipelineId, answeredBy, answered.Status.Discriminator());
            return answered;
        });

    public Result<Attempt> Decline(Id<Pipeline> pipelineId, int runNumber, string stepName, string? comment, string declinedBy)
        => schedulingLock.Run<Result<Attempt>>(() =>
        {
            if (Find(pipelineId, runNumber, stepName).TryPickProblems(out var problems, out var found))
                return problems;
            var (run, _, attempt, _) = found;

            var reason = string.IsNullOrWhiteSpace(comment) ? $"declined by {declinedBy}" : $"declined by {declinedBy}: {comment.Trim()}";
            if (attempts.Decline(attempt.Id, reason).TryPickProblems(out var declineProblems))
                return declineProblems;

            logger.LogInformation("Declined step '{StepName}' in run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' ({DeclinedBy})",
                stepName, run.Number, run.Id, run.PipelineId, declinedBy);
            scheduler.Advance(run.Id);
            return attempts.TryGet(attempt.Id, out var current) ? current : attempt;
        });

    private Result<(Run Run, StepDescriptor Step, Attempt Attempt, Manifest Manifest)> Find(Id<Pipeline> pipelineId, int runNumber, string stepName)
    {
        if (!runs.TryGetByNumber(pipelineId, runNumber, out var run))
            return new NotFoundProblem("Run #{0} not found.", runNumber);
        if (plans.ForRun(run).TryPickProblems(out var planProblems, out var plan))
            return planProblems;
        if (!plan.TryGetStep(stepName, out _, out var step))
            return new NotFoundProblem("Run #{0} has no step '{1}'.", run.Number, stepName);
        if (step.ExternalInputs.Count == 0)
            return new ConflictProblem("Step '{0}' has no external inputs.", stepName);
        var latest = attempts.ListForStep(run.Id, stepName).LastOrDefault();
        if (latest?.Status is not Waiting)
            return new ConflictProblem("Step '{0}' of run #{1} is not waiting for input ({2}).", stepName, run.Number,
                latest is null ? "it has not started" : $"it is {latest.Status.Discriminator()}");
        return (run, step, latest, plan.Manifest);
    }

    private static Result<(string Name, ExternalInput Input)> PickInput(StepDescriptor step, string? name)
    {
        if (name is null)
            return step.ExternalInputs.Count == 1
                ? (step.ExternalInputs.Keys.Single(), step.ExternalInputs.Values.Single())
                : ValidationProblem.ForField("name", "Step '{0}' has several external inputs ({1}); name one.", step.Name, string.Join(", ", step.ExternalInputs.Keys));
        return step.ExternalInputs.TryGetValue(name, out var input)
            ? (name, input)
            : ValidationProblem.ForField("name", "Step '{0}' has no external input '{1}'.", step.Name, name);
    }
}
