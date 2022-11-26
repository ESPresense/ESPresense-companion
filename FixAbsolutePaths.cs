using System.Text;
using System.Text.RegularExpressions;

namespace ESPresense;

public class FixAbsolutePaths
{
    private readonly RequestDelegate _next;

    public FixAbsolutePaths(RequestDelegate next)
    {
        _next = next;
    }

    public async Task Invoke(HttpContext context)
    {
        context.Request.Headers.TryGetValue("X-Ingress-Path", out var ingressPath);
        if (ingressPath.Count == 0)
        {
            await _next(context);
            return;
        }

        bool modifyResponse = true;
        using var ms = new MemoryStream();
        Stream? originBody = context.Response.Body;
        try
        {
            context.Response.Body = ms;
            await _next(context);
            context.Response.Body = originBody;
            if (context.Response.StatusCode == 200)
            {
                var contentType = context.Response.ContentType;
                if ((contentType?.StartsWith("text/html") ?? false) || (contentType?.StartsWith("text/javascript") ?? false))
                {
                    var body = Encoding.UTF8.GetString(ms.ToArray());
                    var newBody = Regex.Replace(body, @"""(?<repl>/(_app|api)/)", m => @$"""{ingressPath[0]}{m.Groups["repl"]}");
                    newBody = Regex.Replace(newBody, "paths: {\"base\":\"\",\"assets\":\"\"}", @$"paths: {{""base"":""{ingressPath[0]}"",""assets"":""""}}");
                    var newBodyBytes = Encoding.UTF8.GetBytes(newBody);
                    context.Response.ContentLength = newBodyBytes.Length;
                    await originBody.WriteAsync(newBodyBytes);
                    modifyResponse = false;
                }
            }
        }
        finally
        {
            if (modifyResponse)
            {
                ms.Seek(0, SeekOrigin.Begin);
                await ms.CopyToAsync(originBody);
            }
        }
    }
}