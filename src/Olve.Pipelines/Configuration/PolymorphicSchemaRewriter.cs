using Microsoft.OpenApi;

namespace Olve.Pipelines.Configuration;

// Microsoft.AspNetCore.OpenApi emits [JsonPolymorphic] hierarchies as `anyOf` +
// discriminator. Kiota's TS generator only emits a real discriminator dispatch
// for `allOf` + discriminator — `anyOf` is treated as intersection, which
// produces a broken union deserializer (all subtype discriminator handlers
// collide and every value except the last-spread one decodes to undefined).
// This rewriter flips the shape to `allOf` per subtype referencing the base.
internal static class PolymorphicSchemaRewriter
{
    public static void RewriteAnyOfToAllOf(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null) return;

        foreach (var (baseName, baseSchemaInterface) in schemas.ToArray())
        {
            if (baseSchemaInterface is not OpenApiSchema baseSchema) continue;
            if (baseSchema.Discriminator is null) continue;
            if (baseSchema.AnyOf is null || baseSchema.AnyOf.Count == 0) continue;

            var discriminatorProperty = baseSchema.Discriminator.PropertyName;
            if (string.IsNullOrEmpty(discriminatorProperty)) continue;

            var subtypeRefs = baseSchema.AnyOf.OfType<OpenApiSchemaReference>().ToList();
            if (subtypeRefs.Count == 0) continue;

            foreach (var subtypeRef in subtypeRefs)
            {
                var subtypeName = subtypeRef.Reference.Id;
                if (subtypeName is null) continue;
                if (!schemas.TryGetValue(subtypeName, out var subtypeInterface)) continue;
                if (subtypeInterface is not OpenApiSchema subtype) continue;

                subtype.Properties?.Remove(discriminatorProperty);
                if (subtype.Required is not null)
                {
                    subtype.Required.Remove(discriminatorProperty);
                    if (subtype.Required.Count == 0) subtype.Required = null;
                }

                subtype.AllOf ??= [];
                subtype.AllOf.Insert(0, new OpenApiSchemaReference(baseName, document));
            }

            baseSchema.AnyOf = null;
            baseSchema.Type = JsonSchemaType.Object;
            baseSchema.Properties ??= new Dictionary<string, IOpenApiSchema>();
            baseSchema.Properties[discriminatorProperty] = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
            };
            baseSchema.Required = new HashSet<string> { discriminatorProperty };
        }
    }

    // ASP.NET emits int32 properties with a regex-validated string union as
    // `type: ["integer", "string"]`. Refitter's NJsonSchema backend then emits
    // a string default for an int C# property, which fails to compile. Drop
    // the string half when we see the int32 format.
    public static void NormalizeIntegerFormats(OpenApiDocument document)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null) return;

        foreach (var schema in schemas.Values)
        {
            if (schema is not OpenApiSchema concrete) continue;
            NormalizeIntegerProperties(concrete);
        }
    }

    private static void NormalizeIntegerProperties(OpenApiSchema schema)
    {
        if (schema.Properties is null) return;

        foreach (var property in schema.Properties.Values)
        {
            if (property is not OpenApiSchema p) continue;
            if (p.Format != "int32" && p.Format != "int64") continue;
            if ((p.Type & JsonSchemaType.Integer) == 0) continue;
            if ((p.Type & JsonSchemaType.String) == 0) continue;

            p.Type &= ~JsonSchemaType.String;
        }
    }
}
