using System.Diagnostics;
using FamilyTree.ServiceDefaults;

namespace FamilyTree.Web;

public static class RequestTelemetryExtensions
{
    public static IApplicationBuilder UseFamilyTreeRequestTelemetry(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var operation = FamilyTelemetry.ClassifyPath(context.Request.Path.Value);
            var stopwatch = Stopwatch.StartNew();
            using var activity = FamilyTelemetry.Start(operation);
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
}
