using System.Diagnostics;

var ci = false;
var runNumber = 0;
var rid = "";
var name = "olve-pipelines";

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--ci":
            ci = true;
            break;
        case "--run-number" when i + 1 < args.Length:
            runNumber = int.Parse(args[++i]);
            break;
        case "--rid" when i + 1 < args.Length:
            rid = args[++i];
            break;
        case "--name" when i + 1 < args.Length:
            name = args[++i];
            break;
        case "--dev":
            ci = false;
            break;
    }
}

var gitHash = RunGit("rev-parse --short HEAD").Trim();

string version;
if (ci)
{
    var now = DateTime.UtcNow;
    version = $"{now.Year}.{now.Month}.{now.Day}.{runNumber}+{gitHash}";
}
else
{
    version = $"0.0.0-dev+{gitHash}";
}

Console.WriteLine($"version={version}");

var artifactName = string.IsNullOrEmpty(rid)
    ? $"{name}-{version}"
    : $"{name}-{version}-{rid}";

Console.WriteLine($"artifact-name={artifactName}");

static string RunGit(string arguments)
{
    var psi = new ProcessStartInfo("git", arguments)
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };

    using var process = Process.Start(psi)!;
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return output;
}
