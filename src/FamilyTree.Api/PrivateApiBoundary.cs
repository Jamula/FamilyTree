using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using System.Net;

namespace FamilyTree.Api;

/// <summary>
/// Private paths fail closed unless explicitly marked endpoints are enabled
/// for loopback development. Unknown paths and caller-supplied identity data
/// never bypass the boundary.
/// </summary>
public static class PrivateApiBoundary
{
    internal sealed class EnabledPrivateEndpoint
    {
        internal static readonly EnabledPrivateEndpoint Instance = new();
        private EnabledPrivateEndpoint() { }
    }

    private static readonly PathString[] PrivatePrefixes =
    [
        "/api/v1/people", "/api/v1/tree", "/api/v1/families", "/api/v1/csrf",
        "/api/v1/memberships", "/api/v1/me", "/api/v1/admin",
        "/api/v1/invitations", "/api/v1/profile-claims",
        "/api/v1/search", "/api/v1/relationships",
        "/api/v1/media", "/api/v1/stories", "/api/v1/gratitude",
        "/api/v1/exports", "/api/v1/imports", "/api/v1/ai",
        "/api/v1/mcp", "/api/v1/private"
    ];

    public static IApplicationBuilder UsePrivateApiBoundary(this IApplicationBuilder app,
        bool privateApiEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (PrivatePrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                var endpoint = context.GetEndpoint();
                if (!privateApiEnabled ||
                    endpoint?.Metadata.GetMetadata<EnabledPrivateEndpoint>() is null ||
                    !context.Request.IsHttps ||
                    !IsLoopbackRequest(context))
                {
                    // Identical status and empty response for every reserved path,
                    // identity header, HTTP verb, hidden ID, and unknown ID.
                    context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                    context.Response.ContentLength = 0;
                    return;
                }
                try
                {
                    await next(context);
                }
                catch (Exception exception) when (exception is not
                    (OutOfMemoryException or AccessViolationException))
                {
                    // Private route failures must not leak IDs or exception details
                    // through the Development exception page or request logger.
                    context.Response.Clear();
                    context.Response.Headers.CacheControl = "no-store";
                    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.Response.ContentLength = 0;
                }
                return;
            }
            await next(context);
        });
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null ||
            !(IPAddress.IsLoopback(address) ||
              (address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(address.MapToIPv4()))))
            return false;

        var host = context.Request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            (IPAddress.TryParse(host, out var hostAddress) && IPAddress.IsLoopback(hostAddress));
    }
}
