using FamilyTree.Domain;
using FamilyTree.ServiceDefaults;
using System.Diagnostics;

namespace FamilyTree.Api;

/// <summary>
/// One authentic API composition root shared by production and loopback
/// integration tests. All private family endpoints remain fail-closed.
/// </summary>
public static class ApiApplication
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddFamilyTreeObservability(builder.Configuration);
        var identityProviders = InteractiveOidc.Configure(builder);
        var app = builder.Build();
        
        // Local, privacy-filtered diagnostics only. No exporter is configured.
        app.Use(async (context, next) =>
        {
            var operation = FamilyTelemetry.ClassifyPath(context.Request.Path.Value);
            var stopwatch = Stopwatch.StartNew();
            using var activity = FamilyTelemetry.Start(operation);
            // Default to failure if downstream middleware throws before writing a status.
            // We must not record a false success for an unhandled exception.
            var statusCode = StatusCodes.Status500InternalServerError;
            try
            {
                await next(context);
                statusCode = context.Response.StatusCode;
            }
            finally
            {
                FamilyTelemetry.Record(operation, statusCode, stopwatch.Elapsed);
            }
        });
        
        
        // This global route gate must remain active until authenticated server policies are reviewed.
        app.UsePrivateApiBoundary();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapInteractiveOidc(identityProviders);
        
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "FamilyTree.Api" }))
           .WithName("Health");
        app.MapGet("/api/v1/capabilities", () => Results.Ok(new {
            genealogyExchange = "GEDCOM 7 (planned adapter)",
            treeDefaults = new TreeOptions(),
            authorization = "not yet configured",
            publicFamilyRecords = false
        })).WithName("Capabilities");
        
        // Fail closed: do not expose records before OIDC and per-record authorization exist.
        app.MapGet("/api/v1/people", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        app.MapGet("/api/v1/tree", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
        return app;
    }
}
