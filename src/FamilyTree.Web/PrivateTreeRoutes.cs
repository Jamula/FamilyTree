using System.Net;
using System.Text;

namespace FamilyTree.Web;

public static class PrivateTreeRoutes
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/private", ShowAsync);
    }

    private static async Task<IResult> ShowAsync(
        HttpContext context,
        PrivateTreeModeOptions options,
        PrivateTreeApiClient api)
    {
        if (!options.Enabled ||
            context.Connection.RemoteIpAddress is not { } address ||
            !IPAddress.IsLoopback(address) ||
            !IsLocalHost(context.Request.Host.Host))
            return Results.NotFound();

        SetPrivacyHeaders(context);
        var status = await api.GetStatusAsync(context, context.RequestAborted);
        return Results.Content(Render(options, status), "text/html; charset=utf-8");
    }

    private static string Render(
        PrivateTreeModeOptions options, PrivateTreeApiStatus status)
    {
        var html = new StringBuilder(2048);
        html.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'>")
            .Append("<meta name='viewport' content='width=device-width,initial-scale=1'>")
            .Append("<title>Private family tree</title><style>")
            .Append("body{font:1rem system-ui;max-width:48rem;margin:2rem auto;padding:0 1rem}")
            .Append("a:focus-visible{outline:3px solid #185abc;outline-offset:3px}")
            .Append("</style></head><body><main><h1>Private family tree</h1>");

        if (!status.Available)
        {
            html.Append("<p role='status'>Private sign-in is unavailable. The configured local API could not be reached securely.</p>");
        }
        else if (!status.Authenticated)
        {
            if (status.SignInProviders.Count == 0)
            {
                html.Append("<p role='status'>No sign-in providers are configured. Private access remains unavailable.</p>");
            }
            else
            {
                html.Append("<p>Sign in through the configured identity provider. After sign-in, return to this page.</p><nav aria-label='Sign in'>");
                foreach (var provider in status.SignInProviders)
                {
                    var signIn = new Uri(options.ApiBaseUri!, "auth/login/" + provider);
                    html.Append("<p><a href='").Append(WebUtility.HtmlEncode(signIn.AbsoluteUri))
                        .Append("'>Sign in with ")
                        .Append(provider == "google" ? "Google" : "Microsoft")
                        .Append("</a></p>");
                }
                html.Append("</nav>");
            }
        }
        else
        {
            html.Append("<p role='status'>Your authenticated session is valid, but private family and tree API endpoints are not available. No family data can be displayed.</p>");
        }

        html.Append("<p><a href='/'>Return to FamilyTree</a></p></main></body></html>");
        return html.ToString();
    }

    private static void SetPrivacyHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; " +
            "base-uri 'none'; frame-ancestors 'none'";
    }

    private static bool IsLocalHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        host is "127.0.0.1" or "::1";
}
