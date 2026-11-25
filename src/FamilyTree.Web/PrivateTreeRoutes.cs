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
        "query", "centerId", "above", "below", "spouses", "siblings", "social",
        "membershipAction", "targetId", "confirmRevoke"
    ];
    private static readonly string[] ToggleFields = ["spouses", "siblings", "social"];

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
        var spouses = ReadToggle(form, "spouses", true);
        var siblings = ReadToggle(form, "siblings", true);
        var social = ReadToggle(form, "social", false);
        if (above is null || below is null ||
            spouses is null || siblings is null || social is null)
            return Results.BadRequest();

        switch (action)
        {
            case "open":
                if (!HasShape(form, ["__RequestVerificationToken", "action", "familyId",
                        "branchId", "above", "below"]))
                    return Results.BadRequest();
                return Results.Content(RenderWorkspace(status, antiforgeryToken,
                    familyId, branchId, null, null, above.Value, below.Value,
                    spouses: spouses.Value, siblings: siblings.Value, social: social.Value),
                    "text/html; charset=utf-8");
            case "search":
                if (!HasShape(form, ["__RequestVerificationToken", "action", "familyId",
                        "branchId", "query", "above", "below"]))
                    return Results.BadRequest();
                if (!Single(form, "query", out var query) ||
                    string.IsNullOrWhiteSpace(query) || query.Trim().Length is < 2 or > 80)
                    return Results.BadRequest();
                if (!await api.EnsureRegisteredAsync(context, context.RequestAborted))
                    return Results.Content(RenderWorkspace(status, antiforgeryToken,
                        familyId, branchId, query.Trim(), null, above.Value, below.Value,
                        unavailable: true, spouses: spouses.Value, siblings: siblings.Value,
                        social: social.Value), "text/html; charset=utf-8");
                var search = await api.SearchAsync(context, familyId, branchId,
                    query, context.RequestAborted);
                var people = search.Kind == PrivateTreeApiResultKind.Success
                    ? search.Value ?? Array.Empty<AuthorizedPersonSummary>()
                    : Array.Empty<AuthorizedPersonSummary>();
                return Results.Content(RenderWorkspace(status, antiforgeryToken,
                    familyId, branchId, query.Trim(), people, above.Value, below.Value,
                    search.Kind == PrivateTreeApiResultKind.Unavailable,
                    spouses: spouses.Value, siblings: siblings.Value, social: social.Value),
                    "text/html; charset=utf-8");
            case "tree":
                if (!HasShape(form, ["__RequestVerificationToken", "action", "familyId",
                        "branchId", "centerId", "above", "below"]))
                    return Results.BadRequest();
                if (!Single(form, "centerId", out var centerText) ||
                    !Guid.TryParse(centerText, out var centerId) || centerId == Guid.Empty)
                    return Results.BadRequest();
                if (!await api.EnsureRegisteredAsync(context, context.RequestAborted))
                    return Results.Content(RenderWorkspace(status, antiforgeryToken,
                        familyId, branchId, null, null, above.Value, below.Value,
                        unavailable: true, spouses: spouses.Value, siblings: siblings.Value,
                        social: social.Value), "text/html; charset=utf-8");
                var tree = await api.ReadTreeAsync(context, familyId, branchId,
                    centerId, context.RequestAborted);
                return Results.Content(RenderWorkspace(status, antiforgeryToken,
                    familyId, branchId, null, null, above.Value, below.Value,
                    tree.Kind != PrivateTreeApiResultKind.Success,
                    tree.Value, spouses: spouses.Value, siblings: siblings.Value,
                    social: social.Value),
                    "text/html; charset=utf-8");
            case "membership":
                if (!HasShape(form, ["__RequestVerificationToken", "action", "familyId",
                        "branchId", "above", "below", "membershipAction", "targetId"],
                        "confirmRevoke"))
                    return Results.BadRequest();
                if (!Single(form, "membershipAction", out var membershipActionText) ||
                    !TryReadMembershipAction(membershipActionText, out var membershipAction) ||
                    !Single(form, "targetId", out var targetText) ||
                    !Guid.TryParse(targetText, out var targetId) || targetId == Guid.Empty ||
                    membershipAction == PrivateTreeMembershipAction.RevokeMember &&
                    (!Single(form, "confirmRevoke", out var revokeConfirmed) ||
                     revokeConfirmed != "true") ||
                    membershipAction != PrivateTreeMembershipAction.RevokeMember &&
                    form.ContainsKey("confirmRevoke"))
                    return Results.BadRequest();
                var membership = await api.EnsureRegisteredAsync(context, context.RequestAborted)
                    ? await api.ManageMembershipAsync(context, familyId,
                        membershipAction, targetId, context.RequestAborted)
                    : new PrivateTreeApiResult<Guid?>(
                        PrivateTreeApiResultKind.Unavailable, null);
                var completed = membership.Kind == PrivateTreeApiResultKind.Success;
                return Results.Content(RenderWorkspace(status, antiforgeryToken,
                    familyId, branchId, null, null, above.Value, below.Value,
                    membershipMessage: completed ? "Membership action accepted." :
                        "Membership request unavailable.",
                    invitationId: membershipAction == PrivateTreeMembershipAction.Invite
                        ? membership.Value : null,
                    spouses: spouses.Value, siblings: siblings.Value, social: social.Value),
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
                true, true, false,
                includeScopeInputs: true);
            html.Append("<p>Family and branch identifiers are entered manually because the API does not provide authorized family or branch listings. The API decides whether each identifier is accessible.</p>");
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
        AuthorizedTreeProjection? tree = null,
        string? membershipMessage = null,
        Guid? invitationId = null,
        bool spouses = true,
        bool siblings = true,
        bool social = false)
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
            spouses, siblings, social,
            includeScopeInputs: true);
        html.Append("<p>Family and branch identifiers are entered manually; access is determined by the API.</p>");
        html.Append("<form method='post' action='/private'>");
        AppendToken(html, token);
        AppendHidden(html, "action", "search");
        AppendHidden(html, "familyId", familyId.ToString("D"));
        AppendHidden(html, "branchId", branchId.ToString("D"));
        AppendHidden(html, "above", above.ToString(CultureInfo.InvariantCulture));
        AppendHidden(html, "below", below.ToString(CultureInfo.InvariantCulture));
        AppendToggleValues(html, spouses, siblings, social);
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
                        person.PersonId, above, below, spouses, siblings, social,
                        "Center on " + person.DisplayName);
                    html.Append("</li>");
                }
                html.Append("</ul>");
            }
        }

        if (membershipMessage is not null)
        {
            html.Append("<p role='status'>").Append(WebUtility.HtmlEncode(membershipMessage));
            if (invitationId is { } createdInvitation)
                html.Append(" Invitation ID: <code>")
                    .Append(WebUtility.HtmlEncode(createdInvitation.ToString("D")))
                    .Append("</code>");
            html.Append("</p>");
        }

        if (tree is not null)
            AppendTree(html, token, familyId, branchId, tree, above, below,
                spouses, siblings, social);

        AppendMembershipControls(html, token, familyId, branchId, above, below,
            spouses, siblings, social);
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
        bool spouses,
        bool siblings,
        bool social,
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
        AppendDisplayToggles(html, spouses, siblings, social);
        html.Append("<button type='submit'>Open family and branch</button></form>");
    }

    private static void AppendTree(
        StringBuilder html,
        string? token,
        Guid familyId,
        Guid branchId,
        AuthorizedTreeProjection tree,
        int above,
        int below,
        bool spouses,
        bool siblings,
        bool social)
    {
        var depthNodes = tree.Nodes.Where(node =>
            node.Generation >= -above && node.Generation <= below).ToArray();
        var depthVisible = depthNodes.Select(node => node.PersonId).ToHashSet();
        var edges = tree.Edges.Where(edge =>
            depthVisible.Contains(edge.FromPersonId) &&
            depthVisible.Contains(edge.ToPersonId) &&
            IsDisplayed(edge.Kind, spouses, siblings, social)).ToArray();
        var siblingConnections = siblings
            ? tree.SiblingConnections.Where(connection =>
                depthVisible.Contains(connection.FirstPersonId) &&
                depthVisible.Contains(connection.SecondPersonId)).ToArray()
            : [];
        var adjacency = new Dictionary<Guid, List<Guid>>();
        void Connect(Guid first, Guid second)
        {
            if (!adjacency.TryGetValue(first, out var firstNeighbors))
                adjacency[first] = firstNeighbors = [];
            if (!adjacency.TryGetValue(second, out var secondNeighbors))
                adjacency[second] = secondNeighbors = [];
            firstNeighbors.Add(second);
            secondNeighbors.Add(first);
        }
        foreach (var edge in edges)
            Connect(edge.FromPersonId, edge.ToPersonId);
        foreach (var connection in siblingConnections)
            Connect(connection.FirstPersonId, connection.SecondPersonId);
        var included = new HashSet<Guid> { tree.CenterPersonId };
        var pending = new Queue<Guid>();
        pending.Enqueue(tree.CenterPersonId);
        while (pending.TryDequeue(out var current))
            if (adjacency.TryGetValue(current, out var neighbors))
                foreach (var neighbor in neighbors)
                    if (included.Add(neighbor))
                        pending.Enqueue(neighbor);
        var nodes = depthNodes.Where(node => included.Contains(node.PersonId)).ToArray();
        var visible = nodes.Select(node => node.PersonId).ToHashSet();
        var graph = new PersonTreeProjection(tree.CenterPersonId,
            nodes.Select(node => new PersonTreeNode(
                new Person(node.PersonId, node.DisplayName), node.Generation)).ToArray(),
            edges.Where(edge => visible.Contains(edge.FromPersonId) &&
                visible.Contains(edge.ToPersonId))
                .Select(edge => new Relationship(edge.RelationshipId, edge.FromPersonId,
                    edge.ToPersonId, edge.Kind)).ToArray(),
            siblingConnections.Where(connection =>
                visible.Contains(connection.FirstPersonId) &&
                visible.Contains(connection.SecondPersonId)).ToArray());
        var options = new TreeOptions(above, below, spouses, siblings, social);
        html.Append(FictionalSvgTree.Render(graph, options, "/private", false,
            recenterLinks: false));
        html.Append("<form method='post' action='/private'>");
        AppendToken(html, token);
        AppendHidden(html, "action", "search");
        AppendHidden(html, "familyId", familyId.ToString("D"));
        AppendHidden(html, "branchId", branchId.ToString("D"));
        AppendHidden(html, "above", above.ToString(CultureInfo.InvariantCulture));
        AppendHidden(html, "below", below.ToString(CultureInfo.InvariantCulture));
        AppendToggleValues(html, spouses, siblings, social);
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
        AppendDisplayToggles(html, spouses, siblings, social);
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
                above, below, spouses, siblings, social,
                "Center on " + node.DisplayName);
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
        bool spouses,
        bool siblings,
        bool social,
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
        AppendToggleValues(html, spouses, siblings, social);
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

    private static void AppendDisplayToggles(
        StringBuilder html, bool spouses, bool siblings, bool social, string idPrefix = "scope")
    {
        html.Append("<fieldset><legend>Displayed relationship types</legend>");
        AppendToggleSelect(html, "spouses", "Spouses and partners", spouses,
            idPrefix + "-spouses");
        AppendToggleSelect(html, "siblings", "Sibling branches", siblings,
            idPrefix + "-siblings");
        AppendHidden(html, "social", social.ToString().ToLowerInvariant());
        html.Append("</fieldset>");
        html.Append("<p>Social connections are unavailable because the authorized tree API currently omits them.</p>");
    }

    private static void AppendToggleSelect(
        StringBuilder html, string name, string label, bool enabled, string id)
    {
        html.Append("<label for='").Append(id).Append("'>")
            .Append(label).Append("</label><select id='").Append(id)
            .Append("' name='").Append(name).Append("'>")
            .Append("<option value='true'").Append(enabled ? " selected>" : ">")
            .Append("Show</option><option value='false'")
            .Append(enabled ? ">" : " selected>")
            .Append("Hide</option></select>");
    }

    private static void AppendToggleValues(
        StringBuilder html, bool spouses, bool siblings, bool social)
    {
        AppendHidden(html, "spouses", spouses.ToString().ToLowerInvariant());
        AppendHidden(html, "siblings", siblings.ToString().ToLowerInvariant());
        AppendHidden(html, "social", social.ToString().ToLowerInvariant());
    }

    private static void AppendMembershipControls(
        StringBuilder html,
        string? token,
        Guid familyId,
        Guid branchId,
        int above,
        int below,
        bool spouses,
        bool siblings,
        bool social)
    {
        html.Append("<section aria-labelledby='membership-controls'><h2 id='membership-controls'>Membership controls</h2>")
            .Append("<p>These actions are checked by the API. No member list is available here; enter an identifier from an authorized workflow.</p>");
        AppendMembershipForm(html, token, "invite", "Invite principal",
            "Principal ID", familyId, branchId, above, below, spouses, siblings, social);
        AppendMembershipForm(html, token, "accept", "Accept invitation",
            "Invitation ID", familyId, branchId, above, below, spouses, siblings, social);
        AppendMembershipForm(html, token, "approve", "Approve member",
            "Member ID", familyId, branchId, above, below, spouses, siblings, social);
        AppendMembershipForm(html, token, "revoke", "Revoke member",
            "Member ID", familyId, branchId, above, below, spouses, siblings, social);
        html.Append("</section>");
    }

    private static void AppendMembershipForm(
        StringBuilder html,
        string? token,
        string action,
        string buttonLabel,
        string targetLabel,
        Guid familyId,
        Guid branchId,
        int above,
        int below,
        bool spouses,
        bool siblings,
        bool social)
    {
        var id = "membership-" + action;
        html.Append("<form method='post' action='/private'>");
        AppendToken(html, token);
        AppendHidden(html, "action", "membership");
        AppendHidden(html, "membershipAction", action);
        AppendHidden(html, "familyId", familyId.ToString("D"));
        AppendHidden(html, "branchId", branchId.ToString("D"));
        AppendHidden(html, "above", above.ToString(CultureInfo.InvariantCulture));
        AppendHidden(html, "below", below.ToString(CultureInfo.InvariantCulture));
        AppendToggleValues(html, spouses, siblings, social);
        html.Append("<label for='").Append(id).Append("'>")
            .Append(targetLabel).Append("</label><input id='").Append(id)
            .Append("' name='targetId' type='text' inputmode='text' autocomplete='off' required>");
        if (action == "revoke")
            html.Append("<label><input name='confirmRevoke' type='checkbox' value='true' required>Confirm revocation</label>");
        html
            .Append("<button type='submit'>").Append(buttonLabel).Append("</button></form>");
    }

    private static bool IsDisplayed(RelationKind kind,
        bool spouses, bool siblings, bool social) =>
        kind switch
        {
            RelationKind.Spouse or RelationKind.Partner => spouses,
            RelationKind.Sibling => siblings,
            RelationKind.Friend or RelationKind.Neighbor or RelationKind.Mentor or
                RelationKind.Other => social,
            _ => true
        };

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

    private static bool HasShape(
        IFormCollection form, string[] required, params string[] optional) =>
        required.All(form.ContainsKey) &&
        form.Keys.All(key => required.Contains(key, StringComparer.Ordinal) ||
            ToggleFields.Contains(key, StringComparer.Ordinal) ||
            optional.Contains(key, StringComparer.Ordinal));

    private static int? ReadDepth(IFormCollection form, string name) =>
        Single(form, name, out var value) &&
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var depth) &&
        depth is >= 0 and <= 4 ? depth : null;

    private static bool? ReadToggle(IFormCollection form, string name, bool defaultValue) =>
        !form.ContainsKey(name) ? defaultValue :
        Single(form, name, out var value) &&
        bool.TryParse(value, out var enabled) ? enabled : null;

    private static bool TryReadMembershipAction(
        string value, out PrivateTreeMembershipAction action)
    {
        action = value switch
        {
            "invite" => PrivateTreeMembershipAction.Invite,
            "accept" => PrivateTreeMembershipAction.AcceptInvitation,
            "approve" => PrivateTreeMembershipAction.ApproveMember,
            "revoke" => PrivateTreeMembershipAction.RevokeMember,
            _ => (PrivateTreeMembershipAction)(-1)
        };
        return Enum.IsDefined(action);
    }

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
