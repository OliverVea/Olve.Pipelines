using System.Text.Json.Nodes;
using Olve.Pipelines.Kubernetes.Harness;
using Olve.Results.TUnit;

namespace Olve.Pipelines.UnitTests.Harness;

public class BuildJobBuilderTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static BuildJobRequest Request(string repo = "OliverVea/Olve.Pipelines", string sha = Sha, string path = ".pipelines", string? secret = "olve-pipeline-11111111222233334444555555555555") =>
        new("olve-build-1111-7", HarnessJobBuilderTests.PipelineId, repo, sha, path, secret, "http://garage/versions/step.tar.gz?sig");

    [Test]
    public async Task BuildJob_MatchesGolden()
    {
        var spec = BuildJobBuilder.Build(Request(), HarnessJobBuilderTests.Options);
        await Assert.That(spec).Succeeded();

        var json = JsonNode.Parse(HarnessJobBuilderTests.Serialize(spec.Value!.Job))!;
        // The script is asserted separately; compare the rest structurally.
        var container = json["spec"]!["template"]!["spec"]!["containers"]![0]!.AsObject();
        await Assert.That(container["args"]![0]!.GetValue<string>()).IsEqualTo(BuildJobBuilder.ShellScript(BuildJobBuilder.Script));
        await Assert.That(container["args"]![0]!.GetValue<string>()).DoesNotContain("\r");
        container["args"] = new JsonArray("<script>");

        await HarnessJobBuilderTests.AssertJsonEqual(json.ToJsonString(), """
            {
              "apiVersion": "batch/v1",
              "kind": "Job",
              "metadata": {
                "name": "olve-build-1111-7",
                "labels": { "app": "olve-pipelines", "job-name": "olve-build-1111-7", "olve-role": "build", "olve-pipeline": "11111111222233334444555555555555" }
              },
              "spec": {
                "template": {
                  "metadata": {
                    "name": "olve-build-1111-7",
                    "labels": { "app": "olve-pipelines", "job-name": "olve-build-1111-7", "olve-role": "build", "olve-pipeline": "11111111222233334444555555555555" }
                  },
                  "spec": {
                    "containers": [
                      {
                        "name": "build",
                        "image": "oven/bun:1.4.3-slim",
                        "command": ["/bin/sh", "-c"],
                        "args": ["<script>"],
                        "env": [
                          { "name": "HOME", "value": "/work" },
                          { "name": "TMPDIR", "value": "/work/tmp" },
                          { "name": "OLVE_REPO", "value": "OliverVea/Olve.Pipelines" },
                          { "name": "OLVE_SHA", "value": "0123456789abcdef0123456789abcdef01234567" },
                          { "name": "OLVE_PIPELINE_PATH", "value": ".pipelines" },
                          { "name": "OLVE_GITHUB_API_URL", "value": "https://api.github.com" },
                          { "name": "OLVE_CODELOAD_URL", "value": "https://codeload.github.com" },
                          { "name": "OLVE_BINARY_URL", "valueFrom": { "secretKeyRef": { "name": "olve-build-1111-7-urls", "key": "OLVE_BINARY_URL", "optional": false } } },
                          { "name": "OLVE_GITHUB_TOKEN", "valueFrom": { "secretKeyRef": { "name": "olve-pipeline-11111111222233334444555555555555", "key": "GITHUB_TOKEN", "optional": true } } }
                        ],
                        "volumeMounts": [{ "name": "work", "mountPath": "/work" }],
                        "securityContext": { "allowPrivilegeEscalation": false },
                        "imagePullPolicy": "IfNotPresent",
                        "resources": { "requests": { "cpu": "500m", "memory": "512Mi" }, "limits": { "memory": "3Gi" } }
                      }
                    ],
                    "volumes": [{ "name": "work", "emptyDir": { "sizeLimit": "4Gi" } }],
                    "restartPolicy": "Never",
                    "runtimeClassName": "gvisor",
                    "securityContext": { "seccompProfile": { "type": "RuntimeDefault" } },
                    "automountServiceAccountToken": false,
                    "enableServiceLinks": false
                  }
                },
                "backoffLimit": 0,
                "ttlSecondsAfterFinished": 604800,
                "activeDeadlineSeconds": 900
              }
            }
            """);
        await Assert.That(spec.Value.UrlSecretData["OLVE_BINARY_URL"]).IsEqualTo("http://garage/versions/step.tar.gz?sig");
    }

    [Test]
    public async Task ShellScript_StripsCarriageReturns()
    {
        // What a CRLF checkout compiles the raw-string script into.
        var crlf = BuildJobBuilder.Script.ReplaceLineEndings("\r\n");

        await Assert.That(crlf).Contains("set -eu\r\n");
        await Assert.That(BuildJobBuilder.ShellScript(crlf)).IsEqualTo(BuildJobBuilder.Script.ReplaceLineEndings("\n"));
        await Assert.That(BuildJobBuilder.ShellScript(crlf)).DoesNotContain("\r");
    }

    [Test]
    public async Task Script_FollowsTheSdkRecipe()
    {
        var script = BuildJobBuilder.Script;

        await Assert.That(script).Contains("(cd \"$dep\" && bun install --frozen-lockfile --production)");
        await Assert.That(script).Contains("cd \"$OLVE_PIPELINE_PATH\"\nbun install --frozen-lockfile\n");
        await Assert.That(script).Contains("bunx olve-build . --out /work/out --sentinels --upload-url \"$OLVE_BINARY_URL\"");
        await Assert.That(script).Contains("--strip-components=1");
        // Inputs are env vars, never spliced into the script.
        await Assert.That(script).DoesNotContain(Sha);
    }

    [Test]
    public async Task Script_InstallsOnlyLocalDependencies()
    {
        var script = BuildJobBuilder.Script;

        // Local file:/link: deps of the binding's package.json, kept inside the repo; no hardcoded sdk/ dir.
        await Assert.That(script).Contains("/^(?:file|link):(.+)$/");
        await Assert.That(script).Contains("points outside the repository");
        await Assert.That(script).Contains("done < /work/local-deps");
        await Assert.That(script).DoesNotContain("cd sdk");
        await Assert.That(script).DoesNotContain("TODO");
    }

    [Test]
    public async Task NoPipelineSecret_NoTokenEnv()
    {
        var spec = BuildJobBuilder.Build(Request(secret: null), HarnessJobBuilderTests.Options).Value!;

        var envNames = spec.Job.Spec.Template.Spec.Containers[0].Env!.Select(e => e.Name).ToArray();
        await Assert.That(envNames).DoesNotContain("OLVE_GITHUB_TOKEN");
        await Assert.That(envNames).Contains("OLVE_BINARY_URL");
    }

    [Test]
    [Arguments("not a repo", Sha, ".pipelines")]
    [Arguments("a/b;rm -rf", Sha, ".pipelines")]
    [Arguments("OliverVea/Olve.Pipelines", "main", ".pipelines")]
    [Arguments("OliverVea/Olve.Pipelines", Sha, "../etc")]
    [Arguments("OliverVea/Olve.Pipelines", Sha, "/abs")]
    [Arguments("OliverVea/Olve.Pipelines", Sha, "a/./b")]
    public async Task RejectsUnsafeInputs(string repo, string sha, string path)
    {
        await Assert.That(BuildJobBuilder.Build(Request(repo, sha, path), HarnessJobBuilderTests.Options)).Failed();
    }

    [Test]
    [Arguments(".")]
    [Arguments(".pipelines")]
    [Arguments("deploy/.pipelines/")]
    public async Task AcceptsRelativePaths(string path)
    {
        await Assert.That(BuildJobBuilder.IsValidPipelinePath(path)).IsTrue();
    }
}
