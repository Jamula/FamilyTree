using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FamilyTree.Web;

public sealed record PrivateTreeApiStatus(
    bool Available,
    IReadOnlyList<string> SignInProviders,
    bool Authenticated,
    bool FamilyAccess);

public sealed class PrivateTreeApiClient(HttpClient http, PrivateTreeModeOptions options)
{
    private const string SessionCookieName = "__Host-FamilyTreeSession";
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

            using var providersJson = await ReadJsonAsync(providersResponse, cancellationToken);
            if (providersJson is null ||
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
                return new(true, providers, false, false);
            if (sessionResponse.StatusCode != HttpStatusCode.OK)
                return Unavailable();

            using var sessionJson = await ReadJsonAsync(sessionResponse, cancellationToken);
            if (sessionJson is null ||
                !sessionJson.RootElement.TryGetProperty("authenticated", out var authenticated) ||
                authenticated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Unavailable();
            var isAuthenticated = authenticated.GetBoolean();
            var hasFamilyAccess = sessionJson.RootElement.TryGetProperty("familyAccess", out var familyAccess) &&
                familyAccess.ValueKind == JsonValueKind.True;
            return new(true, providers, isAuthenticated, isAuthenticated && hasFamilyAccess);
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

    private static async Task<JsonDocument?> ReadJsonAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 4096)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[1024];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0)
                break;
            if (body.Length + count > 4096)
                return null;
            await body.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
        try
        {
            return JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetSessionCookie(HttpContext context)
    {
        var header = context.Request.Headers.Cookie.ToString();
        if (header.Length > 16_384)
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

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address);

    private static string NormalizeHost(string host) =>
        host.Trim('[', ']');

    private static PrivateTreeApiStatus Unavailable() =>
        new(false, Array.Empty<string>(), false, false);
}
