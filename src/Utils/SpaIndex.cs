using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace ESPresense.Utils;

/// <summary>
/// Serves the SPA's index.html for client-side routes such as <c>/devices/{id}</c>.
/// </summary>
/// <remarks>
/// The UI is built with relative paths (<c>./_app/...</c>, and a runtime base of <c>new URL(".", location)</c>)
/// so that it works under any prefix, including Home Assistant ingress, whose prefix the add-on never sees.
/// Relative paths only resolve correctly from the root, though, so a deep link like <c>/devices/{id}</c>
/// loaded <c>/devices/_app/...</c> (404) and rendered a blank page. Rewriting <c>./</c> to the right number of
/// <c>../</c> for the request's depth keeps everything relative while pointing it back at the app root.
/// </remarks>
public static partial class SpaIndex
{
    // Attribute values and module imports that start with "./", and SvelteKit's runtime base.
    [GeneratedRegex("""(?<=(?:href|src)="|import\(")\./""")]
    private static partial Regex RelativeRef();

    [GeneratedRegex("""new URL\("\.", location\)""")]
    private static partial Regex RuntimeBase();

    /// <summary>The relative prefix that leads from <paramref name="requestPath"/>'s directory back to the app root.</summary>
    public static string PrefixFor(string? requestPath)
    {
        var depth = string.IsNullOrEmpty(requestPath) ? 0 : Math.Max(0, requestPath.Count(c => c == '/') - 1);
        return depth == 0 ? "./" : string.Concat(Enumerable.Repeat("../", depth));
    }

    /// <summary>Rewrites index.html's relative references so they resolve from <paramref name="requestPath"/>.</summary>
    public static string Rebase(string html, string? requestPath)
    {
        var prefix = PrefixFor(requestPath);
        if (prefix == "./") return html;
        html = RelativeRef().Replace(html, prefix);
        return RuntimeBase().Replace(html, $"new URL(\"{prefix}\", location)");
    }

    /// <summary>Maps a fallback that serves the rebased index.html for any path that doesn't look like a file.</summary>
    public static IEndpointConventionBuilder MapSpaFallback(this IEndpointRouteBuilder endpoints, string cacheControl)
    {
        var fileProvider = endpoints.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        return endpoints.MapFallback("{*path:nonfile}", async ctx =>
        {
            var file = fileProvider.GetFileInfo("index.html");
            if (!file.Exists)
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var path = ctx.Request.Path.Value;
            var etag = $"\"{file.LastModified.UtcTicks:x}-{file.Length:x}-{PrefixFor(path).Length:x}\"";
            ctx.Response.Headers.CacheControl = cacheControl;
            ctx.Response.Headers.ETag = etag;
            if (ctx.Request.Headers.IfNoneMatch.ToString() == etag)
            {
                ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }

            string html;
            await using (var stream = file.CreateReadStream())
            using (var reader = new StreamReader(stream))
                html = await reader.ReadToEndAsync(ctx.RequestAborted);

            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(Rebase(html, path), ctx.RequestAborted);
        });
    }
}
