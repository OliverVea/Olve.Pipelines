namespace Olve.Pipelines.PipelineSources;

/// <summary>
/// A hardcoded source that defines a directory structure of files.
/// Keys are relative file paths (e.g. "src/main.ts", "config/settings.json"),
/// values are file contents.
/// </summary>
public record HardcodedSource(Dictionary<string, string> Files);
