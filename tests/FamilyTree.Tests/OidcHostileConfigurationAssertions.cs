using System.Runtime.CompilerServices;
using FamilyTree.Api;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;

internal static class OidcHostileConfigurationAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var tenant = Guid.NewGuid();
        var google = new InteractiveOidc.Provider("google", "GoogleOidc",
            "https://accounts.google.com", "synthetic-client", "synthetic-secret",
            "/auth/callback/google");
        var microsoft = new InteractiveOidc.Provider("microsoft", "MicrosoftOidc",
            $"https://login.microsoftonline.com/{tenant:D}/v2.0",
            "synthetic-client", "synthetic-secret", "/auth/callback/microsoft");
        InteractiveOidc.ConfigureProviderOptions(new OpenIdConnectOptions(), google);
        InteractiveOidc.ConfigureProviderOptions(new OpenIdConnectOptions(), microsoft);
        var hostile = new[]
        {
            google with { Authority = "https://accounts.google.com.attacker.invalid" },
            google with { Authority = "https://accounts.google.com/" },
            google with { Callback = "//attacker.invalid/oauth" },
            google with { Scheme = "UntrustedScheme" },
            google with { Name = "microsoft" },
            google with { ClientSecret = "   " },
            google with { ClientId = "" },
            google with { Authority = null! },
            microsoft with { Authority = "https://login.microsoftonline.com/common/v2.0" },
            microsoft with { Authority = "https://login.microsoftonline.com/organizations/v2.0" },
            microsoft with { Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0" },
            microsoft with { Authority = "http://login.microsoftonline.com/" + tenant.ToString("D") + "/v2.0" },
            microsoft with { Callback = "/auth/callback/google" },
        };
        foreach (var attack in hostile)
            Expect<ArgumentException>(() => InteractiveOidc.ConfigureProviderOptions(
                new OpenIdConnectOptions(), attack), "untrusted OIDC provider admitted");

        IConfiguration Config(Dictionary<string, string?> vars) =>
            new ConfigurationBuilder().AddInMemoryCollection(vars).Build();
        Expect<ArgumentNullException>(() => InteractiveOidc.ReadConfiguration(null!),
            "null provider settings accepted");
        Expect<InvalidOperationException>(() => InteractiveOidc.ReadConfiguration(Config(new()
        {
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"] = "",
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"] = ""
        })), "empty Google secrets cannot silently disable configured provider");
        Expect<InvalidOperationException>(() => InteractiveOidc.ReadConfiguration(Config(new()
        {
            ["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"] = "",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"] = "",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"] = ""
        })), "empty Microsoft configuration rejected");
        Expect<InvalidOperationException>(() => InteractiveOidc.ReadConfiguration(Config(new()
        {
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"] = "synthetic",
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"] = " "
        })), "partial OIDC provider configuration rejected");

        Console.WriteLine("PASS OIDC issuer, host, scheme, callback and secret exception tests");
    }

    private static void Expect<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
