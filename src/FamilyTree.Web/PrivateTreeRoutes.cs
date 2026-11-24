using System.Globalization;
using System.Net;
using System.Text;
using FamilyTree.Domain;
using Microsoft.AspNetCore.Antiforgery;

namespace FamilyTree.Web;

public static class PrivateTreeRoutes
{
    private const int MaxFormBytes = 4096;
    private static readonly string[] FormFields =
    [
        "__RequestVerificationToken", "action", "familyId", "branchId",
        "query", "centerId", "above", "below"
    ];

    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/private", ShowAsync);
        app.MapPost("/private", SubmitAsync);
    }

    private static async Task<IResult> ShowAsync(
        HttpContext context,
        PrivateTreeModeOptions options,
        PrivateTreeApiClient api,
        IAntiforgery antiforgery)
    {
        if (!options.Enabled || !IsLocalRequest(context))
            return Results.NotFound();

        SetPrivacyHeaders(context);
        var token = antiforgery.GetAndStoreTokens(context).RequestToken;
        var status = await api.GetStatusAsync(context, context.RequestAborted);
        return Results.Content(Render(options, status, token),
            "text/html; charset=utf-8");
    }

    private static async Task<IResult> SubmitAsync(
        HttpContext context,
        PrivateTreeModeOptions options,
        PrivateTreeApiClient api,
        IAntiforgery antiforgery)
    {
        if (!options.Enabled || !IsLocalRequest(context))
            return Results.NotFound();

        SetPrivacyHeaders(context);
        if (context.Request.ContentLength is null or > MaxFormBytes ||
            context.Request.ContentType is null ||
            !ValidFormContentType(context.Request.ContentType))
            return Results.BadRequest();

        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (InvalidDataException)
        {
            return Results.BadRequest();
        }

        if (form.Files.Count != 0 || form.Keys.Any(key => !FormFields.Contains(key,
                StringComparer.Ordinal)) ||
            form.Keys.Count < 4 ||
            !Single(form, "__RequestVerificationToken", out var token) ||
            string.IsNullOrWhiteSpace(token) ||
            !Single(form, "action", out var action) ||
            !Single(form, "familyId", out var familyText) ||
            !Guid.TryParse(familyText, out var familyId) || familyId == Guid.Empty ||
            !Single(form, "branchId", out var branchText) ||
            !Guid.TryParse(branchText, out var branchId) || branchId == Guid.Empty)
            return Results.BadRequest();

        var antiforgeryToken = antiforgery.GetAndStoreTokens(context).RequestToken;
        var status = await api.GetStatusAsync(context, context.RequestAborted);
        if (!options.Enabled || !status.Available || !status.Authenticated)
            return Results.Content(Render(options, status, antiforgeryToken),
                "text/html; charset=utf-8");

        var above = ReadDepth(form, "above");
        var below = ReadDepth(form, "below");
        if (above is null || below is null)
            return Results.BadRequest();

        switch (action)
        {
            case "open":
                if (!HasExactly(form, "__RequestVerificationToken", "action", "familyId",
                        "branchId", "above", "below"))
                    return Results.BadRequest();
                return Results.Content(RenderWorkspace(status, antiforgeryToken,
                    familyId, branchId, null, null, above.Value, below.Value),
                    "text/html; charset=utf-8");
            case "search":
                if (!HasExactly(form, "__RequestVerificationToken", "action", "familyId",
                        "branchId", "query", "above", "below"))
                    return Results.BadRequest();
                if (!Single(form, "query", out var query) ||
                    string.IsNullOrWhiteSpace(query) || query.Trim().Length is < 2 or > 80)
                    return Results.BadRequest();
                if (!await api.EnsureRegisteredAsync(context, context.RequestAborted))
                    return Results.Content(RenderWorkspace(status, antiforgeryToken,
                        familyId, branchId, query.Trim(), null, above.Value, below.Value,
                        unavailable: true), "text/html; charset=utf-8");
                var search = await api.SearchAsync(context, familyId, branchId,
                    query, context.RequestAborted);
                var people = search.Kind == PrivateTreeApiResultKind.Success
                    ? search.Value ?? Array.Empty<AuthorizedPersonSummary>()
                    : Array.Empty<AuthorizedPersonSummary>();
                return Results.Content(RenderWorkspace(status, antiforgeryToken,
                    familyId, branchId, query.Trim(), people, above.Value, below.Value,
                    search.Kind == PrivateTreeApiResultKind.Unavailable),
                    "text/html; charset=utf-8");
            case "tree":
                if (!HasExactly(form, "__RequestVerificationToken", "action", "familyId",
                        "branchId", "centerId", "above", "below"))
                    return Results.BadRequest();
                if (!Single(form, "centerId", out var centerText) ||
                    !Guid.TryParse(centerText, out var centerId) || centerId == Guid.Empty)
                    return Results.BadRequest();
                if (!await api.EnsureRegisteredAsync(context, context.RequestAborted))
                    return Results.Content(RenderWorkspace(status, antiforgeryToken,
                        familyId, branchId, null, null, above.Value, below.Value,
                        unavailable: true), "text/html; charset=utf-8");
                var tree = await api.ReadTreeAsync(context, familyId, branchId,
                    centerId, context.RequestAborted);
                return Results.Content(RenderWorkspace(status, antiforgeryToken,
                    familyId, branchId, null, null, above.Value, below.Value,
                    tree.Kind != PrivateTreeApiResultKind.Success,
                    tree.Value),
                    "text/html; charset=utf-8");
            default:
                return Results.BadRequest();
        }
    }

    private static string Render(
        PrivateTreeModeOptions options, PrivateTreeApiStatus status, string? token)
    {
        var html = StartPage();
        html.Append("<h1>Private family tree</h1>");
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
            html.Append("<p role='status'>Your sign-in is verified. Family and branch access are checked by the API for each request.</p>");
            AppendScopeForm(html, token, "open", Guid.Empty, Guid.Empty, 4, 4,
                includeScopeInputs: true);
            html.Append("<p>Family and branch identifiers are checked by the API; no access is granted by entering them.</p>");
        }
        html.Append("<p><a href='/'>Return to FamilyTree</a></p>");
        return EndPage(html);
    }

    private static string RenderWorkspace(
        PrivateTreeApiStatus status,
        string? token,
        Guid familyId,
        Guid branchId,
        string? query,
        IReadOnlyList<AuthorizedPersonSummary>? people,
        int above,
        int below,
        bool unavailable = false,
        AuthorizedTreeProjection? tree = null)
    {
        var html = StartPage();
        html.Append("<h1>Private family tree</h1>")
            .Append("<p role='status'>Only records returned by the authenticated API are shown. Unknown, forbidden, or revoked access is not disclosed.</p>");
        if (!status.Available || !status.Authenticated)
        {
            html.Append("<p role='status'>The authenticated private API is unavailable. No family data is shown.</p>");
            html.Append("<p><a href='/private'>Return to sign-in</a></p>");
            return EndPage(html);
        }

        AppendScopeForm(html, token, "open", familyId, branchId, above, below,
            includeScopeInputs: true);
        html.Append("<form method='post' action='/private'>");
        AppendToken(html, token);
        AppendHidden(html, "action", "search");
        AppendHidden(html, "familyId", familyId.ToString("D"));
        AppendHidden(html, "branchId", branchId.ToString("D"));
        AppendHidden(html, "above", above.ToString(CultureInfo.InvariantCulture));
        AppendHidden(html, "below", below.ToString(CultureInfo.InvariantCulture));
        html.Append("<label for='visible-search'>Search visible people</label>")
            .Append("<input id='visible-search' name='query' type='search' minlength='2' maxlength='80' required value='")
            .Append(WebUtility.HtmlEncode(query ?? "")).Append("'>")
            .Append("<button type='submit'>Search</button></form>");

        if (unavailable)
        {
            html.Append("<p role='status'>Private records are unavailable. Try again or check the selected family and branch.</p>");
        }
        else if (people is not null)
        {
            if (people.Count == 0)
                html.Append("<p role='status'>No authorized people matched this search.</p>");
            else
            {
                html.Append("<h2>Authorized search results</h2><ul>");
                foreach (var person in people)
                {
                    html.Append("<li>").Append(WebUtility.HtmlEncode(person.DisplayName));
                    AppendRecenterForm(html, token, familyId, branchId,
                        person.PersonId, above, below,
                        "Center on " + person.DisplayName);
                    html.Append("</li>");
                }
                html.Append("</ul>");
            }
        }

        if (tree is not null)
            AppendTree(html, token, familyId, branchId, tree, above, below);

        html.Append("<p><a href='/'>Return to FamilyTree</a></p>");
        return EndPage(html);
    }

    private static void AppendScopeForm(
        StringBuilder html,
        string? token,
        string action,
        Guid familyId,
        Guid branchId,
        int above,
        int below,
        bool includeScopeInputs)
    {
        html.Append("<form method='post' action='/private'>");
        AppendToken(html, token);
        AppendHidden(html, "action", action);
        if (includeScopeInputs)
        {
            html.Append("<fieldset><legend>Select family and branch</legend>")
                .Append("<label for='family-id'>Family ID</label>")
                .Append("<input id='family-id' name='familyId' type='text' inputmode='text' autocomplete='off' required value='")
                .Append(familyId == Guid.Empty ? "" : familyId.ToString("D")).Append("'>")
                .Append("<label for='branch-id'>Branch ID</label>")
                .Append("<input id='branch-id' name='branchId' type='text' inputmode='text' autocomplete='off' required value='")
                .Append(branchId == Guid.Empty ? "" : branchId.ToString("D")).Append("'>")
                .Append("</fieldset>");
        }
        AppendHidden(html, "above", above.ToString(CultureInfo.InvariantCulture));
        AppendHidden(html, "below", below.ToString(CultureInfo.InvariantCulture));
        html.Append("<button type='submit'>Open family and branch</button></form>");
    }

    private static void AppendTree(
        StringBuilder html,
        string? token,
        Guid familyId,
        Guid branchId,
        AuthorizedTreeProjection tree,
        int above,
        int below)
    {
        var nodes = tree.Nodes.Where(node =>
            node.Generation >= -above && node.Generation <= below).ToArray();
        var visible = nodes.Select(node => node.PersonId).ToHashSet();
        var graph = new PersonTreeProjection(tree.CenterPersonId,
            nodes.Select(node => new PersonTreeNode(
                new Person(node.PersonId, node.DisplayName), node.Generation)).ToArray(),
            tree.Edges.Where(edge => visible.Contains(edge.FromPersonId) &&
                visible.Contains(edge.ToPersonId))
                .Select(edge => new Relationship(edge.RelationshipId, edge.FromPersonId,
                    edge.ToPersonId, edge.Kind)).ToArray(),
            tree.SiblingConnections.Where(connection =>
                visible.Contains(connection.FirstPersonId) &&
                visible.Contains(connection.SecondPersonId)).ToArray());
        var options = new TreeOptions(above, below);
        html.Append(FictionalSvgTree.Render(graph, options, "/private", false,
            recenterLinks: false));
        html.Append("<form method='post' action='/private'>");
        AppendToken(html, token);
        AppendHidden(html, "action", "search");
        AppendHidden(html, "familyId", familyId.ToString("D"));
        AppendHidden(html, "branchId", branchId.ToString("D"));
        AppendHidden(html, "above", above.ToString(CultureInfo.InvariantCulture));
        AppendHidden(html, "below", below.ToString(CultureInfo.InvariantCulture));
        html.Append("<label for='tree-search'>Search visible people</label>")
            .Append("<input id='tree-search' name='query' type='search' minlength='2' maxlength='80' required>")
            .Append("<button type='submit'>Search</button></form>")
            .Append("<form method='post' action='/private' aria-label='Tree depth controls'>");
        AppendToken(html, token);
        AppendHidden(html, "action", "tree");
        AppendHidden(html, "familyId", familyId.ToString("D"));
        AppendHidden(html, "branchId", branchId.ToString("D"));
        AppendHidden(html, "centerId", tree.CenterPersonId.ToString("D"));
        AppendDepthSelect(html, "above", "Generations above", above);
        AppendDepthSelect(html, "below", "Generations below", below);
        html.Append("<button type='submit'>Update depth</button></form>")
            .Append("<h2 id='visible-people'>Visible people</h2>")
            .Append("<table><caption>Authorized people in the selected generations</caption>")
            .Append("<thead><tr><th scope='col'>Person</th><th scope='col'>Relative generation</th><th scope='col'>Action</th></tr></thead><tbody>");
        foreach (var node in nodes)
        {
            html.Append("<tr><td>").Append(WebUtility.HtmlEncode(node.DisplayName))
                .Append("</td><td>").Append(node.Generation.ToString(CultureInfo.InvariantCulture))
                .Append("</td><td>");
            AppendRecenterForm(html, token, familyId, branchId, node.PersonId,
                above, below, "Center on " + node.DisplayName);
            html.Append("</td></tr>");
        }
        html.Append("</tbody></table>");
        if (above == 4 && below == 4)
            html.Append("<p>The API currently returns up to four generations in each direction; lower values limit the displayed authorized projection.</p>");
    }

    private static void AppendRecenterForm(
        StringBuilder html,
        string? token,
        Guid familyId,
        Guid branchId,
        Guid centerId,
        int above,
        int below,
        string label)
    {
        html.Append("<form method='post' action='/private'>");
        AppendToken(html, token);
        AppendHidden(html, "action", "tree");
        AppendHidden(html, "familyId", familyId.ToString("D"));
        AppendHidden(html, "branchId", branchId.ToString("D"));
        AppendHidden(html, "centerId", centerId.ToString("D"));
        AppendHidden(html, "above", above.ToString(CultureInfo.InvariantCulture));
        AppendHidden(html, "below", below.ToString(CultureInfo.InvariantCulture));
        html.Append("<button type='submit'>")
            .Append(WebUtility.HtmlEncode(label)).Append("</button></form>");
    }

    private static void AppendDepthSelect(StringBuilder html,
        string name, string label, int value)
    {
        html.Append("<label for='").Append(name).Append("'>")
            .Append(label).Append("</label><select id='").Append(name)
            .Append("' name='").Append(name).Append("'>");
        for (var depth = 0; depth <= 4; depth++)
            html.Append("<option value='").Append(depth)
                .Append(depth == value ? "' selected>" : "'>")
                .Append(depth).Append("</option>");
        html.Append("</select>");
    }

    private static StringBuilder StartPage()
    {
        var html = new StringBuilder(4096);
        html.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'>")
            .Append("<meta name='viewport' content='width=device-width,initial-scale=1'>")
            .Append("<title>Private family tree</title><style>")
            .Append("body{font:1rem system-ui;max-width:62rem;margin:2rem auto;padding:0 1rem}")
            .Append("form{display:flex;flex-wrap:wrap;gap:.8rem;margin:1rem 0}")
            .Append("label{display:grid;gap:.3rem}input,select,button{font:inherit;padding:.45rem}")
            .Append("a:focus-visible,button:focus-visible,input:focus-visible,select:focus-visible")
            .Append("{outline:3px solid #185abc;outline-offset:3px}")
            .Append("</style></head><body><main>");
        return html;
    }

    private static string EndPage(StringBuilder html) =>
        html.Append("</main></body></html>").ToString();

    private static void AppendToken(StringBuilder html, string? token) =>
        html.Append("<input type='hidden' name='__RequestVerificationToken' value='")
            .Append(WebUtility.HtmlEncode(token ?? "")).Append("'>");

    private static void AppendHidden(StringBuilder html, string name, string value) =>
        html.Append("<input type='hidden' name='").Append(name).Append("' value='")
            .Append(WebUtility.HtmlEncode(value)).Append("'>");

    private static bool Single(IFormCollection form, string name, out string value)
    {
        value = "";
        if (!form.TryGetValue(name, out var values) || values.Count != 1)
            return false;
        value = values[0] ?? "";
        return true;
    }

    private static bool HasExactly(IFormCollection form, params string[] expected) =>
        form.Keys.Count == expected.Length &&
        expected.All(key => form.ContainsKey(key));

    private static int? ReadDepth(IFormCollection form, string name) =>
        Single(form, name, out var value) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var depth) &&
        depth is >= 0 and <= 4 ? depth : null;

    private static bool ValidFormContentType(string contentType)
    {
        var parts = contentType.Split(';', StringSplitOptions.TrimEntries);
        return parts.Length is 1 or 2 &&
            string.Equals(parts[0], "application/x-www-form-urlencoded",
                StringComparison.OrdinalIgnoreCase) &&
            (parts.Length == 1 ||
             string.Equals(parts[1], "charset=utf-8", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLocalRequest(HttpContext context) =>
        context.Connection.RemoteIpAddress is { } address &&
        IPAddress.IsLoopback(address) &&
        context.Request.Host.Host is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    private static void SetPrivacyHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; " +
            "base-uri 'none'; frame-ancestors 'none'";
    }
}
