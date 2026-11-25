using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FamilyTree.Domain;

namespace FamilyTree.Web;

public sealed record PrivateTreeApiStatus(
    bool Available,
    IReadOnlyList<string> SignInProviders,
    bool Authenticated,
    IReadOnlySet<PrivateTreeMembershipAction>? MembershipActions = null)
{
    public bool SupportsMembership(PrivateTreeMembershipAction action) =>
        MembershipActions?.Contains(action) == true;
}

public enum PrivateTreeApiResultKind
{
    Success,
    NotFound,
    Unavailable
}

public sealed record PrivateTreeApiResult<T>(PrivateTreeApiResultKind Kind, T? Value);

public enum PrivateTreeMembershipAction
{
    Invite,
    AcceptInvitation,
    ApproveMember,
    RevokeMember
}

public sealed class PrivateTreeApiClient(HttpClient http, PrivateTreeModeOptions options)
{
    private const string SessionCookieName = "__Host-FamilyTreeSession";
    private const int MaxSearchResults = 25;
    private const int MaxTreeNodes = 10_000;
    private const int MaxTreeEdges = 20_000;
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxDisplayNameLength = 1024;
    private static readonly TimeSpan JsonBodyReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8
    };

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly PrivateTreeModeOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public async Task<PrivateTreeApiStatus> GetStatusAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_options.Enabled || _options.ApiBaseUri is null ||
            !context.Request.IsHttps ||
            !IsLoopback(context.Connection.RemoteIpAddress) ||
            !string.Equals(
                NormalizeHost(context.Request.Host.Host),
                NormalizeHost(_options.ApiBaseUri.Host),
                StringComparison.OrdinalIgnoreCase))
            return Unavailable();

        try
        {
            using var providersResponse = await SendAsync("auth/providers", null, cancellationToken);
            if (providersResponse.StatusCode != HttpStatusCode.OK)
                return Unavailable();

            using var providersJson = await ReadJsonAsync<JsonDocument>(
                providersResponse, 4096, cancellationToken);
            if (providersJson is null ||
                providersJson.RootElement.ValueKind != JsonValueKind.Object ||
                !providersJson.RootElement.TryGetProperty("providers", out var providersElement) ||
                providersElement.ValueKind != JsonValueKind.Array)
                return Unavailable();
            var providers = providersElement.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString())
                .Where(x => x is "google" or "microsoft")
                .Select(x => x!)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            using var sessionResponse = await SendAsync(
                "auth/session", GetSessionCookie(context), cancellationToken);
            if (sessionResponse.StatusCode == HttpStatusCode.Unauthorized)
                return new(true, providers, false);
            if (sessionResponse.StatusCode != HttpStatusCode.OK)
                return Unavailable();

            using var sessionJson = await ReadJsonAsync<JsonDocument>(
                sessionResponse, 4096, cancellationToken);
            if (sessionJson is null ||
                sessionJson.RootElement.ValueKind != JsonValueKind.Object ||
                !sessionJson.RootElement.TryGetProperty("authenticated", out var authenticated) ||
                authenticated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Unavailable();
            var isAuthenticated = authenticated.GetBoolean();
            var membershipActions = isAuthenticated
                ? await GetMembershipActionsAsync(cancellationToken)
                : null;
            return new(true, providers, isAuthenticated, membershipActions);
        }
        catch (HttpRequestException)
        {
            return Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable();
        }
    }

    public async Task<PrivateTreeApiResult<IReadOnlyList<AuthorizedPersonSummary>>> SearchAsync(
        HttpContext context, Guid familyId, Guid branchId, string query,
        CancellationToken cancellationToken)
    {
        if (!CanConnect(context) || familyId == Guid.Empty || branchId == Guid.Empty ||
            string.IsNullOrWhiteSpace(query) || query.Trim().Length is < 2 or > 80)
            return Unavailable<IReadOnlyList<AuthorizedPersonSummary>>();

        var path = $"api/v1/families/{familyId:D}/branches/{branchId:D}/search?q=" +
            Uri.EscapeDataString(query.Trim());
        using var response = await SendPrivateAsync(context, path, cancellationToken);
        if (response is null)
            return Unavailable<IReadOnlyList<AuthorizedPersonSummary>>();
        if (response.StatusCode == HttpStatusCode.NotFound)
            return NotFound<IReadOnlyList<AuthorizedPersonSummary>>();
        if (response.StatusCode != HttpStatusCode.OK)
            return Unavailable<IReadOnlyList<AuthorizedPersonSummary>>();

        var results = await ReadJsonAsync<AuthorizedPersonSummary[]>(response,
            MaxResponseBytes, cancellationToken);
        if (results is null || results.Length > MaxSearchResults ||
            results.Any(person => person is null || person.PersonId == Guid.Empty ||
                string.IsNullOrWhiteSpace(person.DisplayName) ||
                person.DisplayName.Length > MaxDisplayNameLength) ||
            results.Select(person => person.PersonId).Distinct().Count() != results.Length)
            return Unavailable<IReadOnlyList<AuthorizedPersonSummary>>();
        return Success<IReadOnlyList<AuthorizedPersonSummary>>(results);
    }

    public async Task<bool> EnsureRegisteredAsync(
        HttpContext context, CancellationToken cancellationToken)
    {
        if (!CanConnect(context) || GetSessionCookie(context) is not { } sessionCookie)
            return false;

        var antiforgery = await GetApiAntiforgeryAsync(sessionCookie, cancellationToken);
        if (antiforgery is null)
            return false;

        try
        {
            using var registerRequest = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_options.ApiBaseUri!, "api/v1/me"));
            registerRequest.Headers.TryAddWithoutValidation("Cookie",
                sessionCookie + "; " + antiforgery.Value.Cookie);
            registerRequest.Headers.TryAddWithoutValidation("Origin",
                _options.ApiBaseUri!.GetLeftPart(UriPartial.Authority));
            registerRequest.Headers.TryAddWithoutValidation("X-CSRF-TOKEN",
                antiforgery.Value.Token);
            registerRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            registerRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var registerResponse = await _http.SendAsync(registerRequest,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (registerResponse.StatusCode != HttpStatusCode.OK)
                return false;

            using var registration = await ReadJsonAsync<JsonDocument>(
                registerResponse, 4096, cancellationToken);
            return registration is not null &&
                registration.RootElement.ValueKind == JsonValueKind.Object &&
                registration.RootElement.TryGetProperty("principalId", out var principalId) &&
                principalId.ValueKind == JsonValueKind.String &&
                Guid.TryParse(principalId.GetString(), out var parsedId) && parsedId != Guid.Empty;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public async Task<PrivateTreeApiResult<AuthorizedTreeProjection>> ReadTreeAsync(
        HttpContext context, Guid familyId, Guid branchId, Guid centerId,
        CancellationToken cancellationToken)
    {
        if (!CanConnect(context) || familyId == Guid.Empty || branchId == Guid.Empty ||
            centerId == Guid.Empty)
            return Unavailable<AuthorizedTreeProjection>();

        var path = $"api/v1/families/{familyId:D}/branches/{branchId:D}/tree/{centerId:D}";
        using var response = await SendPrivateAsync(context, path, cancellationToken);
        if (response is null)
            return Unavailable<AuthorizedTreeProjection>();
        if (response.StatusCode == HttpStatusCode.NotFound)
            return NotFound<AuthorizedTreeProjection>();
        if (response.StatusCode != HttpStatusCode.OK)
            return Unavailable<AuthorizedTreeProjection>();

        var tree = await ReadJsonAsync<AuthorizedTreeProjection>(response,
            MaxResponseBytes, cancellationToken);
        return IsValidTree(tree, centerId)
            ? Success(tree!)
            : Unavailable<AuthorizedTreeProjection>();
    }

    public async Task<PrivateTreeApiResult<Guid?>> ManageMembershipAsync(
        HttpContext context, Guid familyId, PrivateTreeMembershipAction action,
        Guid targetId, CancellationToken cancellationToken)
    {
        if (!CanConnect(context) || familyId == Guid.Empty || targetId == Guid.Empty ||
            !Enum.IsDefined(action) || GetSessionCookie(context) is not { } sessionCookie)
            return Unavailable<Guid?>();

        if (!(await GetMembershipActionsAsync(cancellationToken)).Contains(action))
            return Unavailable<Guid?>();

        var antiforgery = await GetApiAntiforgeryAsync(sessionCookie, cancellationToken);
        if (antiforgery is null)
            return Unavailable<Guid?>();

        var (method, path) = action switch
        {
            PrivateTreeMembershipAction.Invite =>
                (HttpMethod.Post, $"api/v1/families/{familyId:D}/invitations"),
            PrivateTreeMembershipAction.AcceptInvitation =>
                (HttpMethod.Post, $"api/v1/families/{familyId:D}/invitations/{targetId:D}/accept"),
            PrivateTreeMembershipAction.ApproveMember =>
                (HttpMethod.Post, $"api/v1/families/{familyId:D}/members/{targetId:D}/approve"),
            PrivateTreeMembershipAction.RevokeMember =>
                (HttpMethod.Delete, $"api/v1/families/{familyId:D}/members/{targetId:D}"),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        try
        {
            using var request = new HttpRequestMessage(method,
                new Uri(_options.ApiBaseUri!, path));
            request.Headers.TryAddWithoutValidation("Cookie",
                sessionCookie + "; " + antiforgery.Value.Cookie);
            request.Headers.TryAddWithoutValidation("Origin",
                _options.ApiBaseUri!.GetLeftPart(UriPartial.Authority));
            request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", antiforgery.Value.Token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            if (action == PrivateTreeMembershipAction.Invite)
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { intendedPrincipalId = targetId }),
                    System.Text.Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return NotFound<Guid?>();
            if (action == PrivateTreeMembershipAction.Invite)
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    return Unavailable<Guid?>();
                using var invitation = await ReadJsonAsync<JsonDocument>(
                    response, 4096, cancellationToken);
                return invitation is not null &&
                    invitation.RootElement.ValueKind == JsonValueKind.Object &&
                    invitation.RootElement.TryGetProperty("invitationId", out var invitationId) &&
                    invitationId.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(invitationId.GetString(), out var parsedId) &&
                    parsedId != Guid.Empty
                    ? Success<Guid?>(parsedId)
                    : Unavailable<Guid?>();
            }

            return response.StatusCode == HttpStatusCode.NoContent
                ? Success<Guid?>(null)
                : Unavailable<Guid?>();
        }
        catch (HttpRequestException)
        {
            return Unavailable<Guid?>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Unavailable<Guid?>();
        }
    }

    private async Task<IReadOnlySet<PrivateTreeMembershipAction>> GetMembershipActionsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(
                "api/v1/capabilities", null, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return new HashSet<PrivateTreeMembershipAction>();

            using var capabilities = await ReadJsonAsync<JsonDocument>(
                response, 4096, cancellationToken);
            if (capabilities is null ||
                capabilities.RootElement.ValueKind != JsonValueKind.Object ||
                !capabilities.RootElement.TryGetProperty(
                    "membershipActions", out var actions) ||
                actions.ValueKind != JsonValueKind.Array)
                return new HashSet<PrivateTreeMembershipAction>();

            return actions.EnumerateArray()
                .Where(action => action.ValueKind == JsonValueKind.String)
                .Select(action => action.GetString())
                .Select(action => action switch
                {
                    "invite" => PrivateTreeMembershipAction.Invite,
                    "accept" => PrivateTreeMembershipAction.AcceptInvitation,
                    "approve" => PrivateTreeMembershipAction.ApproveMember,
                    "revoke" => PrivateTreeMembershipAction.RevokeMember,
                    _ => (PrivateTreeMembershipAction?)null
                })
                .Where(action => action.HasValue)
                .Select(action => action!.Value)
                .ToHashSet();
        }
        catch (HttpRequestException)
        {
            return new HashSet<PrivateTreeMembershipAction>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new HashSet<PrivateTreeMembershipAction>();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        string path, string? sessionCookie, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, new Uri(_options.ApiBaseUri!, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        if (sessionCookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
        return await _http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private async Task<HttpResponseMessage?> SendPrivateAsync(
        HttpContext context, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await SendAsync(path, GetSessionCookie(context), cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<(string Cookie, string Token)?> GetApiAntiforgeryAsync(
        string sessionCookie, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                new Uri(_options.ApiBaseUri!, "api/v1/csrf"));
            request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await _http.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
                return null;

            var cookie = ReadResponseCookie(response, "__Host-FamilyTreeAntiforgery");
            using var json = await ReadJsonAsync<JsonDocument>(response, 4096, cancellationToken);
            if (cookie is null || json is null ||
                json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("requestToken", out var requestToken) ||
                requestToken.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(requestToken.GetString()) ||
                requestToken.GetString()!.Length > 4096 ||
                requestToken.GetString()!.Any(character =>
                    !char.IsAscii(character) || char.IsControl(character)))
                return null;
            return (cookie, requestToken.GetString()!);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > maxBytes)
            return default;
        try
        {
            using var bodyDeadline = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            bodyDeadline.CancelAfter(JsonBodyReadTimeout);
            await using var stream = await response.Content.ReadAsStreamAsync(
                bodyDeadline.Token);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, bodyDeadline.Token);
                if (count == 0)
                    break;
                if (body.Length + count > maxBytes)
                    return default;
                await body.WriteAsync(buffer.AsMemory(0, count), bodyDeadline.Token);
            }
            return JsonSerializer.Deserialize<T>(body.ToArray(), JsonOptions);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return default;
        }
        catch (JsonException)
        {
            return default;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static bool IsValidTree(AuthorizedTreeProjection? tree, Guid centerId)
    {
        if (tree is null || tree.CenterPersonId != centerId ||
            tree.Nodes is null || tree.Edges is null || tree.SiblingConnections is null ||
            tree.Nodes.Count is 0 or > MaxTreeNodes ||
            tree.Edges.Count > MaxTreeEdges ||
            tree.Nodes.Any(node => node is null || node.PersonId == Guid.Empty ||
                string.IsNullOrWhiteSpace(node.DisplayName) ||
                node.DisplayName.Length > MaxDisplayNameLength ||
                node.Generation is < -8 or > 8) ||
            tree.Nodes.Select(node => node.PersonId).Distinct().Count() != tree.Nodes.Count)
            return false;

        var visible = tree.Nodes.Select(node => node.PersonId).ToHashSet();
        if (!visible.Contains(centerId) ||
            tree.Nodes.Single(node => node.PersonId == centerId).Generation != 0 ||
            tree.Edges.Any(edge => edge is null || edge.RelationshipId == Guid.Empty ||
                !Enum.IsDefined(edge.Kind) || !visible.Contains(edge.FromPersonId) ||
                !visible.Contains(edge.ToPersonId) || edge.FromPersonId == edge.ToPersonId) ||
            tree.Edges.Select(edge => edge.RelationshipId).Distinct().Count() != tree.Edges.Count ||
            tree.SiblingConnections.Any(connection => connection is null ||
                connection.FirstPersonId == connection.SecondPersonId ||
                !visible.Contains(connection.FirstPersonId) ||
                !visible.Contains(connection.SecondPersonId)))
            return false;

        return true;
    }

    private bool CanConnect(HttpContext context) =>
        _options.Enabled && _options.ApiBaseUri is not null &&
        context.Request.IsHttps &&
        IsLoopback(context.Connection.RemoteIpAddress) &&
        string.Equals(
            NormalizeHost(context.Request.Host.Host),
            NormalizeHost(_options.ApiBaseUri.Host),
            StringComparison.OrdinalIgnoreCase);

    private static string? GetSessionCookie(HttpContext context)
    {
        var header = context.Request.Headers.Cookie.ToString();
        if (header.Length > 16_384 ||
            header.Any(character => !char.IsAscii(character) || char.IsControl(character)))
            return null;

        var selected = new List<string>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in header.Split(';', StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
                continue;
            var name = part[..separator].Trim();
            var isSessionCookie = name == SessionCookieName ||
                name.StartsWith(SessionCookieName + "C", StringComparison.Ordinal) &&
                name[(SessionCookieName.Length + 1)..].Length > 0 &&
                name[(SessionCookieName.Length + 1)..].All(char.IsAsciiDigit);
            if (!isSessionCookie)
                continue;
            if (!names.Add(name))
                return null;
            selected.Add(part);
        }

        var cookie = string.Join("; ", selected);
        return cookie.Length <= 16_384 && cookie.Length > 0 ? cookie : null;
    }

    private static string? ReadResponseCookie(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
            return null;
        var matches = cookies
            .Select(header => header.Split(';', 2)[0].Trim())
            .Where(pair =>
            {
                var separator = pair.IndexOf('=');
                return separator > 0 &&
                    string.Equals(pair[..separator].Trim(), name, StringComparison.Ordinal);
            })
            .Take(2)
            .ToArray();
        return matches.Length == 1 && matches[0].Length <= 4096
            ? matches[0]
            : null;
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    private static string NormalizeHost(string host) =>
        host.Trim('[', ']');

    private static PrivateTreeApiStatus Unavailable() =>
        new(false, Array.Empty<string>(), false);

    private static PrivateTreeApiResult<T> Success<T>(T value) =>
        new(PrivateTreeApiResultKind.Success, value);

    private static PrivateTreeApiResult<T> NotFound<T>() =>
        new(PrivateTreeApiResultKind.NotFound, default);

    private static PrivateTreeApiResult<T> Unavailable<T>() =>
        new(PrivateTreeApiResultKind.Unavailable, default);
}
