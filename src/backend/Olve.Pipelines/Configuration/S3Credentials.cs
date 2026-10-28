namespace Olve.Pipelines.Configuration;

public record S3Credentials(string AccessKey, string SecretKey, string? SessionToken = null);
