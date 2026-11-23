using System.Net;
using System.Text;
using FamilyTree.Domain;
using FamilyTree.SyntheticDemo;

namespace FamilyTree.Web;

/// <summary>
/// Public fictional-only explorer. Never accept data providers or user-owned
/// records here. Actual family views require authenticated API authorization.
/// </summary>
public static class SyntheticTreeExplorer
{
    public static async Task<IResult> ShowAsync(HttpContext http)
    {
        var query = http.Request.Query;
        var people = SyntheticFamilyCatalog.People;
        var centerText = query["center"].ToString();
        var center = Guid.TryParse(centerText, out var parsed) &&
            people.Any(p => p.Id == parsed) ? parsed : people[12].Id;
        var options = new TreeOptions(
            ParseDepth(query["above"].ToString(), 4),
            ParseDepth(query["below"].ToString(), 4),
            ParseToggle(query["spouses"].ToString(), true),
            ParseToggle(query["siblings"].ToString(), true),
            ParseToggle(query["social"].ToString(), false));
        var tree = await PersonTreeProjector.ProjectAsync(
            Guid.Empty, center, people, SyntheticFamilyCatalog.Relationships,
            options, new FictionalOnlyAuthorization(), http.RequestAborted);
        // This route owns zero production data, even when real family data
        // is subsequently implemented elsewhere.
        http.Response.Headers["Cache-Control"] = "no-store";
        http.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Results.Content(Render(people, center, options, tree),
            "text/html; charset=utf-8");
    }

    private static int ParseDepth(string text, int fallback) =>
        int.TryParse(text, out var n) ? Math.Clamp(n, 0, 8) : fallback;

    private static bool ParseToggle(string text, bool fallback) =>
        text switch { "yes" => true, "no" => false, _ => fallback };

    private static string Render(IReadOnlyList<Person> people, Guid center,
        TreeOptions options, PersonTreeProjection? tree)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang='en'><head><meta charset='utf-8'>");
        html.Append("<meta name='viewport' content='width=device-width,initial-scale=1'>");
        html.Append("<title>Fictional FamilyTree explorer</title>");
        html.Append("<style>body{font:1rem system-ui;margin:2rem auto;padding:0 1rem;max-width:62rem}");
        html.Append("form{display:flex;flex-wrap:wrap;gap:.9rem;margin:1.5rem 0}");
        html.Append("label{display:grid;gap:.3rem}select,input,button{font:inherit;padding:.4rem}");
        html.Append("table{width:100%;border-collapse:collapse}td,th{padding:.65rem;border-bottom:1px solid #aaa;text-align:left}");
        html.Append("small{color:#555}caption{text-align:left;margin:.5rem 0}");
        html.Append("a:focus-visible,button:focus-visible,select:focus-visible,input:focus-visible");
        html.Append("{outline:3px solid #185abc;outline-offset:3px}");
        html.Append(".skip{position:absolute;left:-10000px}.skip:focus{position:static}");
        html.Append("</style></head><body><main>");
        html.Append("<a class='skip' href='#visible-people'>Skip to visible people</a>");
        html.Append("<h1>🌳 Fictional family explorer</h1>");
        html.Append("<p><strong>Demonstration data only.</strong> The 32 people are invented; ");
        html.Append("there is no login, production database or access to real family records.</p>");
        html.Append("<form method='get' action='/demo'>");
        html.Append("<label for='center'>Center person<select id='center' name='center'>");
        foreach (var person in people)
        {
            html.Append("<option value='").Append(person.Id.ToString("D")).Append("'");
            if (person.Id == center) html.Append(" selected");
            html.Append(">").Append(WebUtility.HtmlEncode(person.DisplayName)).Append("</option>");
        }
        html.Append("</select></label>");
        html.Append("<label for='above'>Generations above<input id='above' name='above' type='number' min='0' max='8' value='");
        html.Append(options.GenerationsAbove).Append("'></label>");
        html.Append("<label for='below'>Generations below<input id='below' name='below' type='number' min='0' max='8' value='");
        html.Append(options.GenerationsBelow).Append("'></label>");
        Toggle("spouses","Include partners/spouses",options.Spouses);
        Toggle("siblings","Include sibling branches",options.SiblingBranches);
        Toggle("social","Include social connections",options.SocialConnections);
        html.Append("<button type='submit'>Explore family</button></form>");
        html.Append("<details><summary>Keyboard and touch navigation</summary>");
        html.Append("<p>Select a person or use a row's Center link. Keyboard users can Tab");
        html.Append(" through center links, and touch users can tap links.");
        html.Append(" The table is an accessible alternative to pan/zoom.</p></details>");
        html.Append(FictionalSvgTree.Render(tree, options));
        html.Append("<h2 id='visible-people'>Visible fictional people</h2>");
        html.Append("<table><caption>Keyboard-accessible generation list</caption><thead>");
        html.Append("<tr><th scope='col'>Person</th><th scope='col'>Relative generation</th></tr></thead><tbody>");
        foreach (var node in tree?.Nodes ?? Array.Empty<PersonTreeNode>())
        {
            html.Append("<tr><td><a href='/demo?center=").Append(node.Person.Id.ToString("D"))
                .Append("&amp;above=").Append(options.GenerationsAbove)
                .Append("&amp;below=").Append(options.GenerationsBelow)
                .Append("&amp;spouses=").Append(options.Spouses ? "yes" : "no")
                .Append("&amp;siblings=").Append(options.SiblingBranches ? "yes" : "no")
                .Append("&amp;social=").Append(options.SocialConnections ? "yes" : "no")
                .Append("'>Center on ")
                .Append(WebUtility.HtmlEncode(node.Person.DisplayName))
                .Append("</a></td><td>").Append(node.Generation).Append("</td></tr>");
        }
        html.Append("</tbody></table><p><small>Visible relationship links: ");
        html.Append(tree?.Relationships.Count ?? 0).Append("; inferred sibling connections: ");
        html.Append(tree?.InferredSiblingConnections.Count ?? 0).Append(".</small></p>");
        html.Append("<p><a href='/demo/workbench'>Try local fictional editing and review</a></p>");
        html.Append("<p><a href='/'>Return to landing page</a></p></main></body></html>");
        return html.ToString();

        void Toggle(string key, string label, bool selected)
        {
            html.Append("<label for='").Append(key).Append("'>").Append(label);
            html.Append("<select name='").Append(key).Append("' id='").Append(key).Append("'>");
            html.Append("<option value='yes'").Append(selected ? " selected" : "").Append(">Yes</option>");
            html.Append("<option value='no'").Append(!selected ? " selected" : "").Append(">No</option>");
            html.Append("</select></label>");
        }
    }

    private sealed class FictionalOnlyAuthorization : IFamilyAuthorization
    {
        public Task<bool> CanSeePersonAsync(Guid viewerId, Guid personId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SyntheticFamilyCatalog.People.Any(p => p.Id == personId));

        public Task<bool> CanSeeRelationshipAsync(Guid viewerId, Relationship relation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SyntheticFamilyCatalog.Relationships.Any(r => r.Id == relation.Id));
    }
}
