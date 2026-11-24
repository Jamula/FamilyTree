using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FamilyTree.Domain;

namespace FamilyTree.Web;

public sealed record PrivateTreeApiStatus(
    bool Available,
    IReadOnlyList<string> SignInProviders,
    bool Authenticated);

public enum PrivateTreeApiResultKind
{
    Success,
    NotFound,
    Unavailable
}

public sealed record PrivateTreeApiResult<T>(PrivateTreeApiResultKind Kind, T? Value);

public sealed class PrivateTreeApiClient(HttpClient http, PrivateTreeModeOptions options)
{
    private const string SessionCookieName = "__Host-FamilyTreeSession";
    private const int MaxSearchResults = 25;
    private const int MaxTreeNodes = 10_000;
    private const int MaxTreeEdges = 20_000;
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private const int MaxDisplayNameLength = 1024;
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
            return new(true, providers, isAuthenticated);
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

        try
        {
            using var csrfRequest = new HttpRequestMessage(HttpMethod.Get,
                new Uri(_options.ApiBaseUri!, "api/v1/csrf"));
            csrfRequest.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
            csrfRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            csrfRequest.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var csrfResponse = await _http.SendAsync(csrfRequest,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (csrfResponse.StatusCode != HttpStatusCode.OK)
                return false;

            var antiforgeryCookie = ReadResponseCookie(csrfResponse,
                "__Host-FamilyTreeAntiforgery");
            using var csrfJson = await ReadJsonAsync<JsonDocument>(
                csrfResponse, 4096, cancellationToken);
            if (antiforgeryCookie is null || csrfJson is null ||
                csrfJson.RootElement.ValueKind != JsonValueKind.Object ||
                !csrfJson.RootElement.TryGetProperty("requestToken", out var requestToken) ||
                requestToken.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(requestToken.GetString()) ||
                requestToken.GetString()!.Length > 4096 ||
                requestToken.GetString()!.Any(character =>
                    !char.IsAscii(character) || char.IsControl(character)))
                return false;

            using var registerRequest = new HttpRequestMessage(HttpMethod.Post,
                new Uri(_options.ApiBaseUri!, "api/v1/me"));
            registerRequest.Headers.TryAddWithoutValidation("Cookie",
                sessionCookie + "; " + antiforgeryCookie);
            registerRequest.Headers.TryAddWithoutValidation("Origin",
                _options.ApiBaseUri!.GetLeftPart(UriPartial.Authority));
            registerRequest.Headers.TryAddWithoutValidation("X-CSRF-TOKEN",
                requestToken.GetString());
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

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } contentLength &&
            contentLength > maxBytes)
            return default;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken);
                if (count == 0)
                    break;
                if (body.Length + count > maxBytes)
                    return default;
                await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            }
            return JsonSerializer.Deserialize<T>(body.ToArray(), JsonOptions);
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
        string? found = null;
        foreach (var header in cookies)
        {
            var pair = header.Split(';', 2)[0].Trim();
            var separator = pair.IndexOf('=');
            if (separator <= 0 ||
                !string.Equals(pair[..separator].Trim(), name, StringComparison.Ordinal))
                continue;
            if (found is not null || pair.Length > 4096)
                return null;
            found = pair;
        }
        return found;
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
