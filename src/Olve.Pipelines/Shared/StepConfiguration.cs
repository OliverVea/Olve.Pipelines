namespace Olve.Pipelines.Shared;

public record StepConfiguration(string Image, string Script, Dictionary<string, string>? EnvironmentVariables = null);
