using System.Globalization;
using System.Net;
using System.Text;
using FamilyTree.Domain;

namespace FamilyTree.Web;

/// <summary>
/// Accessible, no-JavaScript SVG for the entirely fictional public explorer.
/// The *caller* is exclusively SyntheticTreeExplorer; this helper does not
/// authorize private data or access a user-controlled store.
/// </summary>
public static class FictionalSvgTree
{
    private const int NodeWidth = 142;
    private const int NodeHeight = 54;
    private const int RowHeight = 132;
    private const int ColWidth = 172;

    public static string Render(PersonTreeProjection? tree, TreeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (tree is null || tree.Nodes.Count == 0)
            return "<p>No fictional people available for this view.</p>";

        var levels = tree.Nodes.GroupBy(n => n.Generation)
            .OrderBy(g => g.Key)
            .Select(g => g.OrderBy(n => n.Person.DisplayName, StringComparer.Ordinal).ToArray())
            .ToArray();
        var width = Math.Max(760, (levels.Max(x => x.Length) + 1) * ColWidth);
        var height = (levels.Length + 1) * RowHeight;
        var positions = new Dictionary<Guid, (int X, int Y)>();
        for (var row = 0; row < levels.Length; row++)
        {
            var nodes = levels[row];
            var startX = (width - nodes.Length * ColWidth) / 2;
            for (var col = 0; col < nodes.Length; col++)
                positions[nodes[col].Person.Id] =
                    (startX + col * ColWidth + 15, 45 + row * RowHeight);
        }

        var html = new StringBuilder();
        html.Append("<h2>Family diagram</h2>");
        html.Append("<p>Use a person link to recenter the diagram. On mobile, scroll the ");
        html.Append("diagram sideways. A complete keyboard-accessible table follows.</p>");
        html.Append("<div class='tree-scroll' tabindex='0' role='region' ");
        html.Append("aria-label='Scrollable fictional family diagram' ");
        html.Append("style='overflow:auto;max-width:100%;border:1px solid #aaa;padding:.3rem'>");
        html.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 ")
            .Append(width).Append(' ').Append(height)
            .Append("' width='").Append(width).Append("' height='").Append(height)
            .Append("' aria-labelledby='tree-svg-title tree-svg-description'>");
        html.Append("<title id='tree-svg-title'>Fictional family generation diagram</title>");
        html.Append("<desc id='tree-svg-description'>Links between invented people; ");
        html.Append("select any person to recenter. The following table is an alternative.</desc>");

        // Distinct typed links can connect the same two people. Group them
        // together and bend their paths independently, so an ordinary parent
        // edge cannot hide a dotted friendship or sibling connection.
        var explicitEdges = tree.Relationships
            .Where(edge => positions.ContainsKey(edge.FromPersonId) &&
                positions.ContainsKey(edge.ToPersonId))
            .Select(edge => (From: edge.FromPersonId, To: edge.ToPersonId,
                Kind: edge.Kind, Inferred: false));
        var inferredEdges = tree.InferredSiblingConnections
            .Where(edge => positions.ContainsKey(edge.FirstPersonId) &&
                positions.ContainsKey(edge.SecondPersonId))
            .Select(edge => (From: edge.FirstPersonId, To: edge.SecondPersonId,
                Kind: RelationKind.Sibling, Inferred: true));
        var groups = explicitEdges.Concat(inferredEdges)
            .GroupBy(edge => edge.From.CompareTo(edge.To) < 0
                ? (edge.From, edge.To) : (edge.To, edge.From));
        foreach (var group in groups)
        {
            var edges = group.OrderBy(edge => edge.Kind).ThenBy(edge => edge.Inferred)
                .ToArray();
            foreach (var placement in edges.Select((edge, index) => (
                Edge: edge, Index: index,
                From: positions[edge.From], To: positions[edge.To])))
            {
                var edge = placement.Edge;
                var index = placement.Index;
                var from = placement.From;
                var to = placement.To;
                var fromX = from.X + NodeWidth / 2;
                var fromY = from.Y + NodeHeight / 2;
                var toX = to.X + NodeWidth / 2;
                var toY = to.Y + NodeHeight / 2;
                var bend = (index - (edges.Length - 1) / 2.0) * 32;
                var midX = ((double)fromX + toX) / 2.0;
                var midY = ((double)fromY + toY) / 2.0;
                double controlX, controlY;
                if (fromY == toY)
                {
                    // Lift the curve above same-generation cards, rather than
                    // drawing it straight through any intervening people.
                    controlX = midX;
                    controlY = midY - NodeHeight - 36 - index * 20;
                }
                else
                {
                    double dx = toX - fromX;
                    double dy = toY - fromY;
                    var length = Math.Sqrt(dx * dx + dy * dy);
                    controlX = midX - dy / length * bend;
                    controlY = midY + dx / length * bend;
                }
                var social = edge.Kind is RelationKind.Friend or RelationKind.Neighbor
                    or RelationKind.Mentor or RelationKind.Other;
                var color = social ? "#777" :
                    edge.Kind == RelationKind.Parent ? "#35745b" : "#465c93";
                var dash = edge.Inferred ? "3 4" : edge.Kind switch
                {
                    RelationKind.Parent => "",
                    RelationKind.Sibling => "6 3",
                    RelationKind.Spouse or RelationKind.Partner => "12 5",
                    _ => "2 6"
                };
                static string Decimal(double value) =>
                    value.ToString("0.##", CultureInfo.InvariantCulture);
                html.Append("<path d='M ").Append(fromX).Append(' ').Append(fromY)
                    .Append(" Q ").Append(Decimal(controlX)).Append(' ')
                    .Append(Decimal(controlY)).Append(' ').Append(toX)
                    .Append(' ').Append(toY).Append("' fill='none' stroke='")
                    .Append(color).Append("' stroke-width='2'");
                if (dash.Length != 0)
                    html.Append(" stroke-dasharray='").Append(dash).Append("'");
                html.Append("><title>")
                    .Append(WebUtility.HtmlEncode(edge.Inferred ? "inferred sibling"
                        : edge.Kind.ToString()))
                    .Append(" connection between fictional people</title></path>");
            }
        }

        foreach (var level in levels)
        foreach (var node in level)
        {
            var (x, y) = positions[node.Person.Id];
            var name = node.Person.DisplayName ?? "";
            // No dynamic SVG markup or attributes can be constructed from a
            // name: both text and ARIA labels are HTML-entity-encoded.
            var escaped = WebUtility.HtmlEncode(name);
            var shortName = WebUtility.HtmlEncode(name.Length > 22 ? name[..21] + "…" : name);
            var query = "/demo?center=" + node.Person.Id.ToString("D") +
                "&amp;above=" + options.GenerationsAbove +
                "&amp;below=" + options.GenerationsBelow +
                "&amp;spouses=" + (options.Spouses ? "yes" : "no") +
                "&amp;siblings=" + (options.SiblingBranches ? "yes" : "no") +
                "&amp;social=" + (options.SocialConnections ? "yes" : "no");
            html.Append("<a href='").Append(query).Append("' tabindex='0' aria-label='Center on ")
                .Append(escaped).Append("'>");
            html.Append("<rect x='").Append(x).Append("' y='").Append(y)
                .Append("' width='").Append(NodeWidth).Append("' height='")
                .Append(NodeHeight).Append("' rx='10' stroke='#465c93' ")
                .Append("fill='").Append(node.Person.Id == tree.CenterPersonId ? "#deede5" : "#f7f9fc")
                .Append("' stroke-width='2'/>");
            html.Append("<text x='").Append(x + NodeWidth / 2)
                .Append("' y='").Append(y + 31)
                .Append("' font-family='system-ui' font-size='13' fill='#1f2937' ")
                .Append("text-anchor='middle'>").Append(shortName).Append("</text>");
            html.Append("<title>").Append(escaped).Append(" (generation ")
                .Append(node.Generation).Append(")</title></a>");
        }

        html.Append("</svg></div>");
        html.Append("<p><small>Solid green: parent. Long-dashed blue: partner. ");
        html.Append("Short-dashed blue: sibling. Dotted blue: inferred sibling. ");
        html.Append("Dotted gray: social association. Each link also has a text description.</small></p>");
        return html.ToString();
    }
}
