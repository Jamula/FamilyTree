using System.Net;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;

namespace FamilyTree.Web;

/// <summary>
/// Fictional-only, local-loopback-only workbench. All writes require
/// ASP.NET Core antiforgery validation, a fixed action vocabulary and
/// a bounded in-memory state. This must NEVER host real genealogy.
/// </summary>
public static class SyntheticWorkbenchRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/demo/workbench", (HttpContext http,
            SyntheticReviewWorkbench state, IAntiforgery anti) =>
        {
            if (!Local(http)) return Results.NotFound();
            var token = anti.GetAndStoreTokens(http).RequestToken;
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.ContentSecurityPolicy =
                "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; " +
                "base-uri 'none'; frame-ancestors 'none'";
            return Results.Content(Render(state.Read(), token),
                "text/html; charset=utf-8");
        });

        app.MapPost("/demo/workbench", async (HttpContext http,
            SyntheticReviewWorkbench state, IAntiforgery anti) =>
        {
            if (!Local(http)) return Results.NotFound();
            if (http.Request.ContentLength is > 2048 || !http.Request.HasFormContentType)
                return Results.BadRequest();
            try
            {
                await anti.ValidateRequestAsync(http);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (form.Count != 2 || !form.TryGetValue("action", out var rawAction) ||
                rawAction.Count != 1)
                return Results.BadRequest();
            if (!state.Execute(rawAction.ToString()))
                return Results.Conflict();
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Location = "/demo/workbench";
            return Results.StatusCode(StatusCodes.Status303SeeOther);
        });
    }

    private static bool Local(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } address &&
        IPAddress.IsLoopback(address);

    private static string Render(SyntheticReviewWorkbench.View view, string? token)
    {
        var html = new StringBuilder(4096);
        html.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'>");
        html.Append("<meta name='viewport' content='width=device-width,initial-scale=1'>");
        html.Append("<title>Fictional genealogy review workbench</title><style>");
        html.Append("body{font:1rem system-ui;max-width:54rem;margin:2rem auto;padding:0 1rem}");
        html.Append("button{font:inherit;padding:.65rem;margin:.35rem}");
        html.Append("button:focus-visible,a:focus-visible{outline:3px solid #245ac3}");
        html.Append("table{width:100%;border-collapse:collapse}");
        html.Append("td,th{text-align:left;padding:.55rem;border-bottom:1px solid #aaa}");
        html.Append("</style></head><body><main>");
        html.Append("<h1>Fictional genealogy review workbench</h1>");
        html.Append("<p><strong>Invented records only.</strong> Everything resets when the");
        html.Append(" web process stops. It is limited to this computer, stores no");
        html.Append(" actual relatives, and does not authenticate a real family.</p>");
        html.Append("<p>Step through proposing, independent approval, linking and undo.");
        html.Append(" No names or identifying information can be entered.</p>");
        html.Append("<p><strong>Pending change:</strong> ");
        html.Append(WebUtility.HtmlEncode(view.PendingKind ?? "None"));
        html.Append(". <strong>Guest slots remaining:</strong> ");
        html.Append(view.RemainingGuests);
        html.Append(".</p><form method='post' action='/demo/workbench'>");
        html.Append("<input type='hidden' name='__RequestVerificationToken' value='");
        html.Append(WebUtility.HtmlEncode(token ?? ""));
        html.Append("'>");
        FormAction("propose-person", "1. Propose invented person",
            view.PendingKind is not null || view.RemainingGuests == 0);
        FormAction("propose-link", "2. Propose friendship link",
            view.PendingKind is not null || view.Relationships.Count >= 10);
        FormAction("approve", "3. Approve pending change", view.PendingKind is null);
        FormAction("reject", "Reject pending change", view.PendingKind is null);
        FormAction("undo", "4. Undo last approval",
            view.PendingKind is not null || !view.HasUndo);
        html.Append("</form><h2>Approved fictional people</h2>");
        html.Append("<table><thead><tr><th scope='col'>Person</th></tr></thead><tbody>");
        foreach (var person in view.People)
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode(person.DisplayName))
                .Append("</td></tr>");
        html.Append("</tbody></table><h2>Approved fictional relationships</h2>");
        html.Append("<p>").Append(view.Relationships.Count);
        html.Append(" links have passed independent simulated approval.</p>");
        html.Append("<h2>Review audit</h2><table><thead><tr>");
        html.Append("<th scope='col'>Revision</th><th scope='col'>Decision</th>");
        html.Append("</tr></thead><tbody>");
        foreach (var entry in view.Audit)
            html.Append("<tr><td>").Append(entry.Revision).Append("</td><td>")
                .Append(entry.Reverted ? "Undone" : entry.Approved ? "Approved" : "Rejected")
                .Append("</td></tr>");
        html.Append("</tbody></table><p><a href='/demo'>Return to fictional explorer</a>");
        html.Append("</p></main></body></html>");
        return html.ToString();

        void FormAction(string value, string label, bool disabled)
        {
            html.Append("<button type='submit' name='action' value='");
            html.Append(value).Append("'");
            if (disabled) html.Append(" disabled");
            html.Append(">").Append(WebUtility.HtmlEncode(label)).Append("</button>");
        }
    }
}
