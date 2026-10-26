using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Pipelines.Manifests;
using Olve.Pipelines.Pipelines.Runs;
using Olve.Pipelines.Shared;
using static Olve.Pipelines.Pipelines.Runs.AttemptStatus;

namespace Olve.Pipelines.Pipelines.Scheduling;

/// <summary>Who answers an external input: a source kind the input must list in <c>from</c>, and a name for the record.</summary>
public sealed record Answerer(string Kind, string Name)
{
    public static Answerer Person(string name) => new("person", name);
}

/// <summary>
/// Answers or declines a step that waits for its external inputs. The value is validated against the input's
/// model in the run's pinned manifest; the last missing answer schedules the step. An answerer may only answer
/// inputs whose <c>from</c> lists its kind. Transient; under the scheduling lock.
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
        => Answer(pipelineId, runNumber, stepName, name, value, Answerer.Person(answeredBy));

    public Result<Attempt> Answer(Id<Pipeline> pipelineId, int runNumber, string stepName, string? name, JsonElement value, Answerer by)
        => schedulingLock.Run<Result<Attempt>>(() =>
        {
            if (Find(pipelineId, runNumber, stepName).TryPickProblems(out var problems, out var found))
                return problems;
            var (run, step, attempt, manifest) = found;

            if (PickInput(step, name, by.Kind).TryPickProblems(out var nameProblems, out var input))
                return nameProblems;
            if (attempt.Answers?.ContainsKey(input.Name) == true)
                return new ConflictProblem("Input '{0}' of step '{1}' is already answered.", input.Name, stepName);
            var (inputName, external) = input;
            if (!manifest.Models.TryGetValue(external.Model, out var schemas))
                return new ResultProblem("Model '{0}' of external input '{1}' is not in the manifest.", external.Model, inputName);

            if (JsonSchemaValidator.Validate(schemas.Input ?? schemas.Output, JsonNode.Parse(value.GetRawText()), "value")
                .TryPickProblems(out var invalid, out var validated))
                return invalid;

            var answer = new ExternalAnswer(validated?.ToJsonString() ?? "null", by.Name, timeProvider.GetUtcNow(), by.Kind);
            if (attempts.Answer(attempt.Id, inputName, answer, step.ExternalInputs.Keys.ToList()).TryPickProblems(out var answerProblems, out var answered))
                return answerProblems;

            logger.LogInformation("Answered input '{InputName}' of step '{StepName}' in run #{RunNumber} ('{RunId}') of pipeline '{PipelineId}' ({Source}: {AnsweredBy}); the step is {Status}",
                inputName, stepName, run.Number, run.Id, run.PipelineId, by.Kind, by.Name, answered.Status.Discriminator());
            return answered;
        });

    public Result<Attempt> Decline(Id<Pipeline> pipelineId, int runNumber, string stepName, string? comment, string declinedBy)
        => Decline(pipelineId, runNumber, stepName, comment, Answerer.Person(declinedBy));

    public Result<Attempt> Decline(Id<Pipeline> pipelineId, int runNumber, string stepName, string? comment, Answerer by)
        => schedulingLock.Run<Result<Attempt>>(() =>
        {
            if (Find(pipelineId, runNumber, stepName).TryPickProblems(out var problems, out var found))
                return problems;
            var (run, step, attempt, _) = found;
            if (!step.ExternalInputs.Values.Any(i => i.From.Any(f => f.Kind == by.Kind)))
                return new ConflictProblem("Step '{0}' has no external input a {1} may answer.", stepName, by.Kind);
            var declinedBy = by.Name;

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

    private static Result<(string Name, ExternalInput Input)> PickInput(StepDescriptor step, string? name, string kind)
    {
        if (name is null)
        {
            var candidates = step.ExternalInputs.Where(i => i.Value.From.Any(f => f.Kind == kind)).ToList();
            return candidates.Count switch
            {
                1 => (candidates[0].Key, candidates[0].Value),
                0 => new ConflictProblem("Step '{0}' has no external input a {1} may answer.", step.Name, kind),
                _ => ValidationProblem.ForField("name", "Step '{0}' has several external inputs ({1}); name one.", step.Name, string.Join(", ", candidates.Select(c => c.Key))),
            };
        }
        if (!step.ExternalInputs.TryGetValue(name, out var input))
            return ValidationProblem.ForField("name", "Step '{0}' has no external input '{1}'.", step.Name, name);
        return input.From.Any(f => f.Kind == kind)
            ? (name, input)
            : new ConflictProblem("External input '{0}' of step '{1}' is not answered by a {2} (from: {3}).", name, step.Name, kind, string.Join(", ", input.From.Select(f => f.Kind)));
    }
}
