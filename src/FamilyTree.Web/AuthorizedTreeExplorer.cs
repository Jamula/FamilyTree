using System.Net;
using System.Text;
using FamilyTree.Domain;

namespace FamilyTree.Web;

/// <summary>
/// A request-scoped reader bound by the caller to a verified principal and
/// trusted family and branch. It must return only field-redacted projections.
/// </summary>
public interface IAuthorizedTreeReader
{
    Task<AuthorizedTreeProjection?> ReadAsync(Guid center, TreeOptions options,
        CancellationToken cancellationToken);
}

/// <summary>
/// Rendering adapter only: no route or reader is registered by the web host.
/// A future host must enforce authentication and membership before calling it.
/// </summary>
public static class AuthorizedTreeExplorer
{
    public static async Task<IResult> ShowAsync(HttpContext http, IAuthorizedTreeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        http.Response.Headers.ContentSecurityPolicy =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

        var query = http.Request.Query;
        if (!Guid.TryParse(query["center"].ToString(), out var center) || center == Guid.Empty)
            return Results.NotFound();

        var options = new TreeOptions(
            Depth(query["above"].ToString()), Depth(query["below"].ToString()),
            Toggle(query["spouses"].ToString(), true),
            Toggle(query["siblings"].ToString(), true),
            Toggle(query["social"].ToString(), false));
        var tree = await reader.ReadAsync(center, options, http.RequestAborted);
        if (tree is null || tree.CenterPersonId != center ||
            !tree.Nodes.Any(node => node.PersonId == center))
            return Results.NotFound();

        var search = query["search"].ToString();
        return Results.Content(Render(tree, options, search),
            "text/html; charset=utf-8");
    }

    private static int Depth(string text) =>
        int.TryParse(text, out var value) ? Math.Clamp(value, 0, 8) : 4;

    private static bool Toggle(string text, bool fallback) =>
        text switch { "yes" => true, "no" => false, _ => fallback };

    private static string Render(AuthorizedTreeProjection tree, TreeOptions options, string search)
    {
        var graph = new PersonTreeProjection(tree.CenterPersonId,
            tree.Nodes.Select(n => new PersonTreeNode(new Person(n.PersonId, n.DisplayName),
                n.Generation)).ToArray(),
            tree.Edges.Select(e => new Relationship(e.RelationshipId, e.FromPersonId,
                e.ToPersonId, e.Kind)).ToArray(), tree.SiblingConnections);
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'>")
            .Append("<meta name='viewport' content='width=device-width,initial-scale=1'>")
            .Append("<title>Family tree</title><style>")
            .Append("body{font:1rem system-ui;margin:2rem auto;padding:0 1rem;max-width:62rem}")
            .Append("form{display:flex;flex-wrap:wrap;gap:.9rem;margin:1.5rem 0}")
            .Append("label{display:grid;gap:.3rem}select,input,button{font:inherit;padding:.4rem}")
            .Append("a:focus-visible,button:focus-visible,select:focus-visible,input:focus-visible")
            .Append("{outline:3px solid #185abc;outline-offset:3px}</style></head><body><main>")
            .Append("<h1>Family tree</h1><form method='get' action=''>")
            .Append("<label for='center'>Center person<select id='center' name='center'>");
        foreach (var node in tree.Nodes)
        {
            html.Append("<option value='").Append(node.PersonId.ToString("D")).Append("'")
                .Append(node.PersonId == tree.CenterPersonId ? " selected" : "")
                .Append(">").Append(WebUtility.HtmlEncode(node.DisplayName)).Append("</option>");
        }
        html.Append("</select></label>")
            .Append("<label for='above'>Generations above<input id='above' name='above' type='number' min='0' max='8' value='")
            .Append(options.GenerationsAbove).Append("'></label>")
            .Append("<label for='below'>Generations below<input id='below' name='below' type='number' min='0' max='8' value='")
            .Append(options.GenerationsBelow).Append("'></label>");
        AppendToggle("spouses", "Include partners/spouses", options.Spouses);
        AppendToggle("siblings", "Include sibling branches", options.SiblingBranches);
        AppendToggle("social", "Include social connections", options.SocialConnections);
        html.Append("<label for='search'>Search visible people<input id='search' name='search' value='")
            .Append(WebUtility.HtmlEncode(search)).Append("'></label>")
            .Append("<button type='submit'>Explore family</button></form>")
            .Append("<p>Use Tab to reach person links, or tap a link. The list below is an alternative to scrolling the diagram.</p>")
            .Append(FictionalSvgTree.Render(graph, options, "", false))
            .Append("<h2>Visible people</h2><table><caption>Keyboard-accessible generation list</caption>")
            .Append("<thead><tr><th scope='col'>Person</th><th scope='col'>Relative generation</th></tr></thead><tbody>");
        foreach (var node in tree.Nodes.Where(n =>
            n.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)))
        {
            html.Append("<tr><td><a href='?center=")
                .Append(node.PersonId.ToString("D"))
                .Append("&amp;above=").Append(options.GenerationsAbove)
                .Append("&amp;below=").Append(options.GenerationsBelow)
                .Append("&amp;spouses=").Append(options.Spouses ? "yes" : "no")
                .Append("&amp;siblings=").Append(options.SiblingBranches ? "yes" : "no")
                .Append("&amp;social=").Append(options.SocialConnections ? "yes" : "no")
                .Append("'>Center on ").Append(WebUtility.HtmlEncode(node.DisplayName))
                .Append("</a></td><td>").Append(node.Generation).Append("</td></tr>");
        }
        return html.Append("</tbody></table></main></body></html>").ToString();

        void AppendToggle(string key, string label, bool selected)
        {
            html.Append("<label for='").Append(key).Append("'>").Append(label)
                .Append("<select name='").Append(key).Append("' id='").Append(key).Append("'>")
                .Append("<option value='yes'").Append(selected ? " selected" : "").Append(">Yes</option>")
                .Append("<option value='no'").Append(!selected ? " selected" : "").Append(">No</option>")
                .Append("</select></label>");
        }
    }
}
