using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Olve.Pipelines.Configuration;

/// <summary>
/// The image's files all carry a 1970 mtime, so without an explicit policy a browser treats a cached
/// index.html as fresh for years and keeps loading an old bundle. The page and the docs revalidate on
/// every load; Vite's content-hashed /assets never change.
/// </summary>
public static class StaticCaching
{
    public static StaticFileOptions Options(IContentTypeProvider? contentTypes = null)
    {
        var options = new StaticFileOptions
        {
            OnPrepareResponse = context => context.Context.Response.Headers[HeaderNames.CacheControl] =
                context.Context.Request.Path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache",
        };
        if (contentTypes is not null)
            options.ContentTypeProvider = contentTypes;
        return options;
    }
}
