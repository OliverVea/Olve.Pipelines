using Microsoft.OpenApi;
using Olve.MinimalApi;

namespace Olve.Pipelines.Configuration;

public static class JsonConfiguration
{
    public static void ConfigureJson(this WebApplicationBuilder builder)
    {
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));
        builder.Services.WithPathJsonConversion();
        builder.Services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer((schema, context, _) =>
            {
                var type = context.JsonTypeInfo.Type;
                if ((type.IsGenericType && type.GetGenericTypeDefinition().FullName == "Olve.Utilities.Ids.Id`1")
                    || type.FullName == "Olve.Utilities.Ids.Id")
                {
                    schema.Properties?.Clear();
                    schema.Type = JsonSchemaType.String;
                    schema.Format = "uuid";
                }
                return Task.CompletedTask;
            });
        });
    }

    public static void MapJson(this WebApplication app)
    {
        app.MapOpenApi();
    }
}
