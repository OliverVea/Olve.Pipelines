using System.Text.Json.Nodes;
using Olve.Pipelines.Recorder.Recordings;

namespace Olve.Pipelines.Recorder.Capture;

/// <summary>
/// Finished Jobs on a throwaway host, shaped like harness Jobs. Everything goes through host.sh, which
/// only reaches that host's own containers: nothing here reads a kubeconfig or runs kubectl on the machine.
/// </summary>
internal static class K3sCapture
{
    private const string Namespace = "olve-runners";

    private static readonly string[] JobElisions = ["metadata.annotations", "metadata.labels", "metadata.managedFields", "metadata.uid", "spec"];

    private static readonly string[] PodElisions =
    [
        "items.metadata.annotations", "items.metadata.generateName", "items.metadata.labels", "items.metadata.managedFields",
        "items.metadata.ownerReferences", "items.metadata.uid", "items.spec", "items.status.conditions", "items.status.containerStatuses.containerID",
        "items.status.containerStatuses.image", "items.status.containerStatuses.imageID", "items.status.containerStatuses.lastState",
        "items.status.containerStatuses.state.terminated.containerID", "items.status.containerStatuses.volumeMounts", "items.status.hostIP", "items.status.hostIPs",
        "items.status.initContainerStatuses.containerID", "items.status.initContainerStatuses.image", "items.status.initContainerStatuses.imageID",
        "items.status.initContainerStatuses.lastState", "items.status.initContainerStatuses.state.terminated.containerID", "items.status.initContainerStatuses.volumeMounts", "items.status.podIP", "items.status.podIPs",
        "metadata",
    ];

    public static IReadOnlyList<Recording> Capture(string repoRoot, int slot, DateOnly today)
    {
        var version = SystemPins.Single(repoRoot, SystemPins.K3s);
        string Kubectl(string? input, params string[] arguments) =>
            Captures.Run(Path.Combine(repoRoot, "tools/real-system/host.sh"), ["kubectl", slot.ToString(), "--", .. arguments], repoRoot, input);

        var reported = JsonNode.Parse(Kubectl(null, "get", "--raw", "/version"))!["gitVersion"]!.GetValue<string>().Replace('+', '-');
        if (reported != version)
            throw new InvalidOperationException($"host slot {slot} runs {reported}, the repo pins {version}");

        var source = $"rancher/k3s:{version} on tools/real-system/host.sh";
        var recordings = new List<Recording>();
        foreach (var (name, command, deadline, condition) in new[]
        {
            ("complete", "exit 0", 120, "complete"),
            ("deadline", "sleep 300", 8, "failed"),
        })
        {
            var job = $"recorder-{name}";
            Kubectl(null, "-n", Namespace, "delete", "job", job, "--ignore-not-found", "--wait=true");
            Kubectl(Manifest(job, command, deadline), "apply", "-f", "-");
            Kubectl(null, "-n", Namespace, "wait", $"--for=condition={condition}", $"job/{job}", "--timeout=180s");

            var jobUrl = $"/apis/batch/v1/namespaces/{Namespace}/jobs/{job}";
            var podsUrl = $"/api/v1/namespaces/{Namespace}/pods?labelSelector=batch.kubernetes.io/job-name={job}";
            recordings.Add(Exchange($"job-{name}", jobUrl, JobElisions));
            recordings.Add(Exchange($"pods-{name}", podsUrl, PodElisions));
            Kubectl(null, "-n", Namespace, "delete", "job", job, "--wait=false");
        }
        return recordings;

        Recording Exchange(string name, string url, string[] elided)
        {
            var body = Scrubber.Scrub(Scrubber.Elide(JsonNode.Parse(Kubectl(null, "get", "--raw", url)), elided));
            return new Recording(
                SystemPins.K3s, name, source, today, version, elided,
                new RecordedRequest("GET", url, new Dictionary<string, string>()),
                new RecordedResponse(200, new Dictionary<string, string>(), body));
        }
    }

    private static string Manifest(string name, string command, int deadlineSeconds) => $$"""
        {
          "apiVersion": "batch/v1",
          "kind": "Job",
          "metadata": { "name": "{{name}}", "namespace": "{{Namespace}}" },
          "spec": {
            "backoffLimit": 0,
            "activeDeadlineSeconds": {{deadlineSeconds}},
            "template": {
              "spec": {
                "restartPolicy": "Never",
                "automountServiceAccountToken": false,
                "initContainers": [{ "name": "olve-init", "image": "busybox:1.37", "command": ["sh", "-c", "exit 0"] }],
                "containers": [{ "name": "step", "image": "busybox:1.37", "command": ["sh", "-c", "{{command}}"] }]
              }
            }
          }
        }
        """;
}
