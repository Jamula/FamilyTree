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

        foreach (var edge in tree.Relationships)
        {
            if (!positions.TryGetValue(edge.FromPersonId, out var first) ||
                !positions.TryGetValue(edge.ToPersonId, out var second))
                continue;
            var social = edge.Kind is RelationKind.Friend or RelationKind.Neighbor
                or RelationKind.Mentor or RelationKind.Other;
            var parent = edge.Kind == RelationKind.Parent;
            html.Append("<line x1='").Append(first.X + NodeWidth / 2)
                .Append("' y1='").Append(first.Y + NodeHeight / 2)
                .Append("' x2='").Append(second.X + NodeWidth / 2)
                .Append("' y2='").Append(second.Y + NodeHeight / 2)
                .Append("' stroke='").Append(social ? "#777" : parent ? "#35745b" : "#465c93")
                .Append("' stroke-width='2'");
            if (social) html.Append(" stroke-dasharray='5 5'");
            html.Append("/>");
        }

        // Sibling branches may be inferred from two visible people sharing an
        // authorized parent link, even when depth hides the parent nodes.
        foreach (var sibling in tree.InferredSiblingConnections)
        {
            if (!positions.TryGetValue(sibling.FirstPersonId, out var first) ||
                !positions.TryGetValue(sibling.SecondPersonId, out var second))
                continue;
            html.Append("<line x1='").Append(first.X + NodeWidth / 2)
                .Append("' y1='").Append(first.Y + NodeHeight / 2)
                .Append("' x2='").Append(second.X + NodeWidth / 2)
                .Append("' y2='").Append(second.Y + NodeHeight / 2)
                .Append("' stroke='#465c93' stroke-width='2' stroke-dasharray='3 4'/>");
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
        html.Append("<p><small>Green links represent parents, blue links represent ");
        html.Append("partnerships or siblings; dotted gray links represent social associations.</small></p>");
        return html.ToString();
    }
}
