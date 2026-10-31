using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Olve.Pipelines.Api;

namespace Olve.Pipelines.RealSystemTests.Hosting;

/// <summary>An answer that isn't asserted to be a success: its status and the problem's <c>detail</c>.</summary>
public sealed record ApiReply(HttpStatusCode Status, string? Detail);

/// <summary>The API as a test uses it. <see cref="Http"/> reaches whatever has no method here yet.</summary>
public sealed class PipelinesApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = PipelinesJsonContext.Default.Options;

    public HttpClient Http => http;

    public async Task<PipelineBinding> CreatePipelineAsync(string repo) =>
        await ReadAsync<PipelineBinding>(await http.PostAsJsonAsync("api/pipelines", new CreatePipeline { Repo = repo }, Json));

    public async Task SetPollingAsync(Guid pipelineId, bool polls) =>
        Ensure(await http.PutAsJsonAsync($"api/pipelines/{pipelineId}/binding/polling", new SetBindingPolling { Polls = polls }, Json));

    public async Task SetSecretAsync(Guid pipelineId, string name, string value) =>
        Ensure(await http.PutAsJsonAsync($"api/pipelines/{pipelineId}/secrets/{name}", new SetSecret { Value = value }, Json));

    public async Task<ApiReply> ReconcileAsync(Guid pipelineId) =>
        await ReplyAsync(await http.PostAsync($"api/pipelines/{pipelineId}/binding/reconcile?wait=true", null));

    public async Task<IReadOnlyList<Pipeline>> ListPipelinesAsync() =>
        await ReadAsync<IReadOnlyList<Pipeline>>(await http.GetAsync("api/pipelines"));

    public async Task<PipelineBinding> GetBindingAsync(Guid pipelineId) =>
        await ReadAsync<PipelineBinding>(await http.GetAsync($"api/pipelines/{pipelineId}/binding"));

    public async Task<ApiReply> GetPipelineReplyAsync(Guid pipelineId) =>
        await ReplyAsync(await http.GetAsync($"api/pipelines/{pipelineId}"));

    public async Task<Pipeline> GetPipelineAsync(Guid pipelineId) =>
        await ReadAsync<Pipeline>(await http.GetAsync($"api/pipelines/{pipelineId}"));

    public async Task<ApiReply> DeletePipelineAsync(Guid pipelineId) =>
        await ReplyAsync(await http.DeleteAsync($"api/pipelines/{pipelineId}"));

    public async Task<PipelineVersions> ListVersionsAsync(Guid pipelineId) =>
        await ReadAsync<PipelineVersions>(await http.GetAsync($"api/pipelines/{pipelineId}/versions"));

    public async Task<Run> StartRunAsync(Guid pipelineId, JsonObject input)
    {
        var start = await ReadAsync<RunStart>(await http.PostAsync($"api/pipelines/{pipelineId}/runs", JsonBody(input)));
        return start.Run ?? throw new InvalidOperationException($"The run did not start: {start.Outcome}");
    }

    public async Task<ApiReply> StartRunReplyAsync(Guid pipelineId, JsonObject input) =>
        await ReplyAsync(await http.PostAsync($"api/pipelines/{pipelineId}/runs", JsonBody(input)));

    public async Task<Run> GetRunAsync(Guid pipelineId, int number) =>
        await ReadAsync<Run>(await http.GetAsync($"api/pipelines/{pipelineId}/runs/{number}"));

    public async Task<string> GetStepOutputAsync(Guid pipelineId, int number, string step) =>
        await ReadTextAsync(await http.GetAsync($"api/pipelines/{pipelineId}/runs/{number}/steps/{step}/output"));

    public async Task<string> GetStepFileAsync(Guid pipelineId, int number, string step, string path) =>
        await ReadTextAsync(await http.GetAsync($"api/pipelines/{pipelineId}/runs/{number}/steps/{step}/files/{path}"));

    public async Task<string> GetStepLogsAsync(Guid pipelineId, int number, string step) =>
        await ReadTextAsync(await http.GetAsync($"api/pipelines/{pipelineId}/runs/{number}/steps/{step}/logs"));

    private static StringContent JsonBody(JsonObject body) => new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Ensure(response);
        return await response.Content.ReadFromJsonAsync<T>(Json)
               ?? throw new InvalidOperationException($"{response.RequestMessage?.RequestUri} answered with no body.");
    }

    private static async Task<string> ReadTextAsync(HttpResponseMessage response)
    {
        Ensure(response);
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<ApiReply> ReplyAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
            return new ApiReply(response.StatusCode, null);
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            return new ApiReply(response.StatusCode, JsonNode.Parse(body)?["detail"]?.GetValue<string>());
        }
        catch (JsonException)
        {
            return new ApiReply(response.StatusCode, body);
        }
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new HttpRequestException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} answered {(int)response.StatusCode}: {body}",
                null, response.StatusCode);
        }
    }
}
