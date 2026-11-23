using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using FamilyTree.Domain;
using System.Security;
using System.Security.Claims;

namespace FamilyTree.Api;

/// <summary>
/// Interactive OIDC login is NOT a family membership. Even successful
/// authentication must never bypass the pre-MVP private API boundary.
/// </summary>
public static class InteractiveOidc
{
    public sealed record Provider(string Name, string Scheme, string Authority,
        string ClientId, string ClientSecret, string Callback);

    public static IReadOnlyList<Provider> Configure(WebApplicationBuilder builder)
    {
        var providers = ReadConfiguration(builder.Configuration);
        var auth = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(ConfigureCookieOptions);
        builder.Services.AddAuthorization();
        foreach (var provider in providers)
        {
            // Maintained ASP.NET Core OIDC middleware performs actual token validation.
            auth.AddOpenIdConnect(provider.Scheme, options =>
                ConfigureProviderOptions(options, provider));
        }
        return providers;
    }

    /// <summary>Pure configuration seam for negative-case unit tests.</summary>
    public static void ConfigureCookieOptions(CookieAuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Cookie.Name = "__Host-FamilyTreeSession";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.SlidingExpiration = false;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    }

    /// <summary>Provider config does not authenticate users on its own.</summary>
    public static void ConfigureProviderOptions(OpenIdConnectOptions options, Provider provider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.ClientId) ||
            string.IsNullOrWhiteSpace(provider.ClientSecret))
            throw new ArgumentException("OIDC provider configuration is incomplete.", nameof(provider));
        var isGoogle = provider.Name == "google" && provider.Scheme == "GoogleOidc" &&
            provider.Authority == "https://accounts.google.com" &&
            provider.Callback == "/auth/callback/google";
        var isMicrosoft = provider.Name == "microsoft" && provider.Scheme == "MicrosoftOidc" &&
            provider.Callback == "/auth/callback/microsoft" &&
            provider.Authority is not null && provider.Authority.StartsWith("https://login.microsoftonline.com/",
                StringComparison.Ordinal);
        if (!isGoogle && !isMicrosoft)
            throw new ArgumentException("OIDC provider is not recognized.", nameof(provider));
        // Defense-in-depth: require provider-specific exact trusted issuer and
        // audience even when callers bypass ReadConfiguration in tests.
        _ = new OidcConfigurationGuard(
            [new TrustedOidcIssuer(provider.Authority!, provider.ClientId)]);
        options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.Authority = provider.Authority;
        options.ClientId = provider.ClientId;
        options.ClientSecret = provider.ClientSecret;
        options.CallbackPath = provider.Callback;
        options.ResponseType = "code";
        options.UsePkce = true;
        options.RequireHttpsMetadata = true;
        options.SaveTokens = false;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.Events.OnTokenValidated = context =>
        {
            // Framework token verification precedes this callback. Do not
            // let caller-supplied headers/claims construct a trusted session.
            if (context.Principal?.Identity is not ClaimsIdentity identity)
            {
                context.Fail("Verified OIDC identity required.");
                return Task.CompletedTask;
            }
            var subjectClaims = context.Principal.FindAll("sub").ToArray();
            if (subjectClaims.Length == 0)
                subjectClaims = context.Principal.FindAll(ClaimTypes.NameIdentifier).ToArray();
            if (subjectClaims.Length != 1)
            {
                context.Fail("Unique OIDC subject required.");
                return Task.CompletedTask;
            }
            try
            {
                VerifiedOidcSession.Stamp(identity, provider,
                    context.SecurityToken?.Issuer, subjectClaims[0].Value);
            }
            catch (SecurityException)
            {
                context.Fail("OIDC subject binding denied.");
            }
            return Task.CompletedTask;
        };
        options.Events.OnRemoteFailure = context =>
        {
            context.HandleResponse();
            context.Response.Redirect("/auth/error");
            return Task.CompletedTask;
        };
    }

    public static IReadOnlyList<Provider> ReadConfiguration(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var providers = new List<Provider>();
        // Explicitly configured but blank settings are an error, not a
        // silent provider disablement that hides deployment mistakes.
        static bool Partial(params string?[] parts) => parts.Any(x => x is not null);
        static void RequireComplete(params string?[] parts)
        {
            if (parts.Any(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException("An OIDC provider is partially configured.");
        }

        var gId = config["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"];
        var gSecret = config["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"];
        if (Partial(gId, gSecret))
        {
            RequireComplete(gId, gSecret);
            providers.Add(new("google", "GoogleOidc", "https://accounts.google.com",
                gId!, gSecret!, "/auth/callback/google"));
        }

        var tenant = config["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"];
        var mId = config["FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"];
        var mSecret = config["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"];
        if (Partial(tenant, mId, mSecret))
        {
            RequireComplete(tenant, mId, mSecret);
            if (!Guid.TryParse(tenant, out var id) || id == Guid.Empty)
                throw new InvalidOperationException("An explicit Microsoft tenant GUID is required.");
            providers.Add(new("microsoft", "MicrosoftOidc",
                $"https://login.microsoftonline.com/{id:D}/v2.0",
                mId!, mSecret!, "/auth/callback/microsoft"));
        }
        return providers;
    }

    public static void MapInteractiveOidc(this WebApplication app,
        IReadOnlyList<Provider> providers)
    {
        app.MapGet("/auth/providers", () =>
            Results.Ok(new { providers = providers.Select(x => x.Name).ToArray(),
                membershipAuthorization = false }));
        app.MapGet("/auth/login/{provider}", (string provider) =>
        {
            var item = providers.FirstOrDefault(p =>
                string.Equals(p.Name, provider, StringComparison.Ordinal));
            return item is null ? Results.NotFound() :
                Results.Challenge(new AuthenticationProperties
                {
                    RedirectUri = "/auth/session"
                }, [item.Scheme]);
        });
        app.MapGet("/auth/session", async (HttpContext context) =>
            await VerifiedOidcSession.ReadAsync(context, providers)
                ? Results.Ok(new { authenticated = true, familyAccess = false })
                : Results.Unauthorized());
        app.MapGet("/auth/error", () =>
            Results.Problem("Authentication could not be completed.",
                statusCode: StatusCodes.Status401Unauthorized));
    }
}
