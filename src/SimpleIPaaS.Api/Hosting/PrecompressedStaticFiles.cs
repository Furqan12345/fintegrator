using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace SimpleIPaaS.Api.Hosting;

// Serves the .br / .gz files that `dotnet publish` already writes next to every Blazor asset. UseStaticFiles
// ignores them, so without this the 30 MB uncompressed client was sent on every cold load (the same bytes
// compress to about 3.4 MB with brotli). Brotli is preferred, then gzip, otherwise the original file.
public static class PrecompressedStaticFiles
{
    public static IApplicationBuilder UsePrecompressedStaticFiles(this IApplicationBuilder app, IFileProvider webRoot)
    {
        return app.Use(async (context, next) =>
        {
            if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                && context.Request.Path.HasValue
                && !context.Request.Path.Value!.EndsWith('/')
                && context.Request.Path.Value!.IndexOf('.', context.Request.Path.Value.LastIndexOf('/') + 1) >= 0)
            {
                var accepted = context.Request.Headers.AcceptEncoding.ToString();
                var original = context.Request.Path.Value!;
                string? encoding = null;
                if (accepted.Contains("br", StringComparison.OrdinalIgnoreCase) && webRoot.GetFileInfo(original + ".br").Exists)
                {
                    encoding = "br";
                }
                else if (accepted.Contains("gzip", StringComparison.OrdinalIgnoreCase) && webRoot.GetFileInfo(original + ".gz").Exists)
                {
                    encoding = "gzip";
                }

                if (encoding != null)
                {
                    context.Request.Path = original + (encoding == "br" ? ".br" : ".gz");
                    context.Response.Headers.ContentEncoding = encoding;
                    context.Response.Headers.Append(HeaderNames.Vary, HeaderNames.AcceptEncoding);
                }
            }

            await next();
        });
    }
}

// Maps "app.wasm.br" to the content type of "app.wasm" so the compressed twin is served as the original type.
public sealed class PrecompressedContentTypeProvider : IContentTypeProvider
{
    private readonly FileExtensionContentTypeProvider _inner;

    public PrecompressedContentTypeProvider(FileExtensionContentTypeProvider inner) => _inner = inner;

    public bool TryGetContentType(string subpath, out string contentType)
    {
        if (subpath.EndsWith(".br", StringComparison.OrdinalIgnoreCase) || subpath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
        {
            subpath = subpath[..^3];
        }

        return _inner.TryGetContentType(subpath, out contentType!);
    }
}
