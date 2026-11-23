using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FamilyTree.Api;

/// <summary>
/// Pre-MVP hard stop: no real-family route may respond while authenticated
/// membership, per-record authorization and audit are unimplemented.
/// Deliberately ignores caller-supplied identity headers and bearer text.
/// A future replacement must require #9/#10 integration and privacy tests.
/// </summary>
public static class PrivateApiBoundary
{
    private static readonly PathString[] PrivatePrefixes =
    [
        "/api/v1/people", "/api/v1/tree", "/api/v1/families",
        "/api/v1/memberships", "/api/v1/me", "/api/v1/admin",
        "/api/v1/invitations", "/api/v1/profile-claims",
        "/api/v1/search", "/api/v1/relationships",
        "/api/v1/media", "/api/v1/stories", "/api/v1/gratitude",
        "/api/v1/exports", "/api/v1/imports", "/api/v1/ai",
        "/api/v1/mcp", "/api/v1/private"
    ];

    public static IApplicationBuilder UsePrivateApiBoundary(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            if (PrivatePrefixes.Any(prefix => context.Request.Path.StartsWithSegments(prefix)))
            {
                // Identical status and empty response for every reserved path,
                // identity header, HTTP verb, hidden ID, and unknown ID.
                context.Response.StatusCode = StatusCodes.Status501NotImplemented;
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.ContentLength = 0;
                return;
            }
            await next(context);
        });
    }
}
