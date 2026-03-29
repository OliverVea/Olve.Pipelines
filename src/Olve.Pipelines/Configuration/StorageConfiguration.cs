using Amazon.S3;

namespace Olve.Pipelines.Configuration;

public static class StorageConfiguration
{
    public static void ConfigureStorage(this WebApplicationBuilder builder)
    {
        var endpoint = builder.Configuration["Storage:Endpoint"];
        var accessKey = builder.Configuration["Storage:AccessKey"];
        var secretKey = builder.Configuration["Storage:SecretKey"];
        var bucket = builder.Configuration["Storage:Bucket"] ?? "olve-pipelines";

        builder.Services.AddSingleton(new StorageOptions(bucket));

        if (endpoint is not null)
        {
            builder.Services.AddSingleton<IAmazonS3>(new AmazonS3Client(
                accessKey,
                secretKey,
                new AmazonS3Config
                {
                    ServiceURL = endpoint,
                    ForcePathStyle = true,
                }));
        }
    }
}

public record StorageOptions(string Bucket);
