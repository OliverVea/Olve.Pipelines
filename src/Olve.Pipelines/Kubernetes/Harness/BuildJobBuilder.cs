using System.Text.RegularExpressions;
using Olve.Pipelines.Pipelines;

namespace Olve.Pipelines.Kubernetes.Harness;

/// <summary>Everything one build/describe Job is built from (spec §5.1, sdk/README.md "Build tool").</summary>
/// <param name="JobName">K8s Job name (deterministic per build so a restart can reattach).</param>
/// <param name="Repo">GitHub <c>owner/name</c>.</param>
/// <param name="CommitSha">Full 40-hex commit id.</param>
/// <param name="PipelinePath">The binding path inside the repo (the package root holding <c>package.json</c>), e.g. <c>.pipelines</c>.</param>
/// <param name="PipelineSecretName">The pipeline's K8s Secret; its <paramref name="TokenSecretKey"/> key (optional) authenticates the
/// tarball fetch through the GitHub API. Null = anonymous download from codeload.github.com (public repos; no API rate limit).</param>
/// <param name="BinaryPutUrl">Pre-signed PUT for the build's <c>step.tar.gz</c> (<see cref="Pipelines.StorageLayout.BuildBinaryKey"/>).</param>
/// <param name="TokenSecretKey">Key of the GitHub token in the pipeline Secret (the binding's credentials key); null = <see cref="HarnessOptions.BuildTokenSecretKey"/>.</param>
public sealed record BuildJobRequest(
    string JobName,
    Id<Pipeline> PipelineId,
    string Repo,
    string CommitSha,
    string PipelinePath,
    string? PipelineSecretName,
    string BinaryPutUrl,
    string? TokenSecretKey = null);

/// <summary>
/// Builds the build/describe Job: on a glibc Bun image, fetch the repo tarball at the commit, install,
/// <c>olve-build</c> (compile + describe), PUT <c>step.tar.gz</c> to the pre-signed URL and print the
/// manifest between <c>---OLVE-MANIFEST-BEGIN/END---</c> (read back with
/// <see cref="HarnessSentinels.TryExtractManifest"/>). Same runtime class / labels / pod policy as step
/// Jobs. The script is a constant; repo, sha and path arrive as env vars. Pure.
/// </summary>
public static partial class BuildJobBuilder
{
    public const string ContainerName = "build";
    public const string BinaryUrlKey = "OLVE_BINARY_URL";

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DnsLabelRegex();

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoRegex();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex ShaRegex();

    /// <summary>
    /// The build recipe. Exit codes: olve-build's (0 ok, 1 build failure, 64 usage, 78 pipeline invalid),
    /// plus 65 when the tarball cannot be fetched and 78 for an unreadable <c>package.json</c> or a local dependency outside the repo. The tarball has one top-level directory
    /// (<c>&lt;owner&gt;-&lt;repo&gt;-&lt;sha7&gt;/</c>), stripped on extract. oven/bun:*-slim has no curl/wget,
    /// so Bun itself downloads.
    /// </summary>
    public const string Script = """
        set -eu
        mkdir -p /work/src /work/out /work/tmp
        cd /work
        bun -e '(async () => {
        const repo = process.env.OLVE_REPO, sha = process.env.OLVE_SHA, token = process.env.OLVE_GITHUB_TOKEN;
        // With a token: the API tarball endpoint (private repos). Without: codeload, which has no API rate limit.
        const url = token ? `${process.env.OLVE_GITHUB_API_URL}/repos/${repo}/tarball/${sha}` : `${process.env.OLVE_CODELOAD_URL}/${repo}/tar.gz/${sha}`;
        const headers = { accept: "application/vnd.github+json", "user-agent": "olve-pipelines" };
        if (token) headers.authorization = `Bearer ${token}`;
        for (let i = 1; ; i++) {
          let res, err;
          try { res = await fetch(url, { headers }); } catch (e) { err = e; }
          if (res && res.ok) { await Bun.write("/work/repo.tar.gz", res); break; }
          const transient = !res || res.status >= 500 || res.status === 408 || res.status === 429;
          if (!transient || i >= 4) {
            console.error(`olve-build-job: cannot fetch ${repo}@${sha}: ${res ? "HTTP " + res.status : err}${token ? "" : " (no token: a private repository needs one)"}`);
            process.exit(65);
          }
          await Bun.sleep(2000 * i);
        }
        })().catch((e) => { console.error(`olve-build-job: ${e}`); process.exit(65); });'
        tar -xzf repo.tar.gz -C /work/src --strip-components=1
        rm -f repo.tar.gz
        cd /work/src
        # A local file:/link: dependency (an in-repo SDK, "file:../sdk") resolves to its source directory, so
        # its own dependencies are installed there. Registry dependencies (the npm alias) need nothing extra.
        bun -e '(async () => {
        const { resolve } = require("node:path"), { existsSync } = require("node:fs");
        const dir = resolve("/work/src", process.env.OLVE_PIPELINE_PATH);
        const pkg = await Bun.file(`${dir}/package.json`).json();
        for (const deps of [pkg.dependencies, pkg.devDependencies, pkg.optionalDependencies]) {
          for (const [name, spec] of Object.entries(deps ?? {})) {
            const m = /^(?:file|link):(.+)$/.exec(String(spec));
            if (!m) continue;
            const target = resolve(dir, m[1]);
            if (!existsSync(`${target}/package.json`)) continue;
            if (target !== "/work/src" && !target.startsWith("/work/src/")) {
              console.error(`olve-build-job: dependency ${name} (${spec}) points outside the repository`);
              process.exit(78);
            }
            console.log(target);
          }
        }
        })().catch((e) => { console.error(`olve-build-job: cannot read package.json: ${e}`); process.exit(78); });' > /work/local-deps
        while IFS= read -r dep; do
          (cd "$dep" && bun install --frozen-lockfile --production)
        done < /work/local-deps
        cd "$OLVE_PIPELINE_PATH"
        bun install --frozen-lockfile
        exec bunx olve-build . --out /work/out --sentinels --upload-url "$OLVE_BINARY_URL"
        """;

    /// <summary>
    /// A script as <c>sh -c</c> gets it: LF line endings only. A CRLF checkout (core.autocrlf on Windows)
    /// compiles CRs into the raw string, and <c>set -eu\r</c> then fails the Job.
    /// </summary>
    public static string ShellScript(string script) => script.ReplaceLineEndings("\n");

    /// <summary>Per-build URL Secret name.</summary>
    public static string UrlSecretName(string jobName) => $"{jobName}-urls";

    /// <summary>A relative path inside the repo: no leading <c>/</c>, no <c>.</c>/<c>..</c> segments (except the root <c>.</c>), no <c>\</c> or NUL.</summary>
    public static bool IsValidPipelinePath(string path)
    {
        if (path == ".") return true;
        if (path.Length == 0 || path.StartsWith('/') || path.Contains('\\') || path.Contains('\0'))
            return false;
        return path.TrimEnd('/').Split('/').All(s => s.Length > 0 && s != "." && s != "..");
    }

    public static Result<HarnessJobSpec> Build(BuildJobRequest request, HarnessOptions options)
    {
        if (!DnsLabelRegex().IsMatch(request.JobName) || request.JobName.Length > 57)
            return new ResultProblem("Invalid build Job name '{0}'.", request.JobName);
        if (!RepoRegex().IsMatch(request.Repo))
            return new ResultProblem("Invalid GitHub repository '{0}'.", request.Repo);
        if (!ShaRegex().IsMatch(request.CommitSha))
            return new ResultProblem("Invalid commit sha '{0}' (want 40 lowercase hex).", request.CommitSha);
        if (!IsValidPipelinePath(request.PipelinePath))
            return new ResultProblem("Invalid pipeline path '{0}'.", request.PipelinePath);

        var urlSecretName = UrlSecretName(request.JobName);
        var secretData = new Dictionary<string, string>(StringComparer.Ordinal) { [BinaryUrlKey] = request.BinaryPutUrl };

        var env = new List<K8sEnvVar>
        {
            new("HOME", "/work"),
            new("TMPDIR", "/work/tmp"),
            new("OLVE_REPO", request.Repo),
            new("OLVE_SHA", request.CommitSha),
            new("OLVE_PIPELINE_PATH", request.PipelinePath),
            new("OLVE_GITHUB_API_URL", options.GitHubApiUrl),
            new("OLVE_CODELOAD_URL", options.GitHubCodeloadUrl),
            K8sEnvVar.FromSecret(BinaryUrlKey, urlSecretName, BinaryUrlKey),
        };
        if (request.PipelineSecretName is not null)
            env.Add(K8sEnvVar.FromSecret("OLVE_GITHUB_TOKEN", request.PipelineSecretName, request.TokenSecretKey ?? options.BuildTokenSecretKey, optional: true));

        var labels = new Dictionary<string, string>
        {
            ["app"] = "olve-pipelines",
            ["job-name"] = request.JobName,
            ["olve-role"] = "build",
            ["olve-pipeline"] = request.PipelineId.Value.Value.ToString("N"),
        };

        var container = new K8sContainer(
            ContainerName,
            options.BunImage,
            Command: ["/bin/sh", "-c"],
            Args: [ShellScript(Script)],
            Env: env.ToArray(),
            VolumeMounts: [new K8sVolumeMount("work", "/work")],
            SecurityContext: HarnessSecurity.Container,
            ImagePullPolicy: "IfNotPresent",
            Resources: new K8sResourceRequirements(
                Requests: new() { ["cpu"] = options.BuildCpuRequest, ["memory"] = options.BuildMemoryRequest },
                Limits: new() { ["memory"] = options.BuildMemoryLimit }));

        var job = new K8sJobManifest(
            "batch/v1",
            "Job",
            new K8sMetadata(request.JobName, labels),
            new K8sJobSpecBody(
                new K8sPodTemplateSpec(
                    new K8sMetadata(request.JobName, labels),
                    new K8sPodSpec(
                        Containers: [container],
                        Volumes: [new K8sVolume("work", new K8sEmptyDir(options.BuildScratchSizeLimit))],
                        RestartPolicy: "Never",
                        RuntimeClassName: options.RuntimeClassName,
                        SecurityContext: HarnessSecurity.Pod,
                        AutomountServiceAccountToken: false,
                        EnableServiceLinks: false)),
                BackoffLimit: 0,
                TtlSecondsAfterFinished: options.TtlSecondsAfterFinished,
                ActiveDeadlineSeconds: options.BuildTimeoutSeconds));

        return new HarnessJobSpec(job, urlSecretName, secretData);
    }
}
