using System.Runtime.CompilerServices;
using System.Net;
using FamilyTree.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static class InteractiveOidcEndpointAssertions
{
    [ModuleInitializer]
    internal static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        await ExerciseAsync(new Dictionary<string,string?>(),configured:false);
        await ExerciseAsync(new Dictionary<string,string?>
        {
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"]="synthetic-client",
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"]="synthetic-secret",
            ["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"]=Guid.NewGuid().ToString("D"),
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"]="synthetic-ms-client",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"]="synthetic-ms-secret"
        },configured:true);
        Console.WriteLine("PASS local OIDC endpoint, cookie and provider options tests");
    }

    private static async Task ExerciseAsync(IDictionary<string,string?> vars,bool configured)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName="Development"
        });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(vars);
        var providers=InteractiveOidc.Configure(builder);
        Check(providers.Count==(configured?2:0),"provider registration state");

        await using var app=builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapInteractiveOidc(providers);
        await app.StartAsync();
        using var client=app.GetTestClient();

        using var providerResponse=await client.GetAsync("/auth/providers");
        Check(providerResponse.StatusCode==HttpStatusCode.OK,"provider listing");
        var body=await providerResponse.Content.ReadAsStringAsync();
        Check(body.Contains("\"membershipAuthorization\":false",StringComparison.Ordinal),
            "authentication alone cannot grant genealogy access");
        Check(configured
            ? body.Contains("google",StringComparison.Ordinal) &&
              body.Contains("microsoft",StringComparison.Ordinal) &&
              !body.Contains("synthetic-secret",StringComparison.Ordinal)
            : body.Contains("\"providers\":[]",StringComparison.Ordinal),
            "only configured safe provider names disclosed");

        using var session=await client.GetAsync("/auth/session");
        Check(session.StatusCode==HttpStatusCode.Unauthorized,
            "anonymous user has no authenticated session");
        using var missing=await client.GetAsync("/auth/login/unconfigured");
        Check(missing.StatusCode==HttpStatusCode.NotFound,
            "unknown provider returns 404");
        using var error=await client.GetAsync("/auth/error");
        Check(error.StatusCode==HttpStatusCode.Unauthorized &&
              !(await error.Content.ReadAsStringAsync()).Contains("synthetic-secret",
                  StringComparison.Ordinal),"generic authentication failure response");

        var cookies=app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        Check(cookies.Cookie.Name=="__Host-FamilyTreeSession" &&
              cookies.Cookie.HttpOnly &&
              cookies.Cookie.SecurePolicy==Microsoft.AspNetCore.Http.CookieSecurePolicy.Always &&
              cookies.SlidingExpiration==false &&
              cookies.ExpireTimeSpan==TimeSpan.FromHours(8),
              "secure cookie policy without persistent token storage");
        if(configured)
        {
            var monitor=app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
            foreach(var provider in providers)
            {
                var options=monitor.Get(provider.Scheme);
                Check(options.RequireHttpsMetadata && options.UsePkce &&
                      options.ResponseType=="code" && !options.SaveTokens &&
                      options.TokenValidationParameters.ValidateIssuer &&
                      options.TokenValidationParameters.ValidateAudience &&
                      options.CallbackPath==provider.Callback &&
                      options.ClientId==provider.ClientId,
                      "exact OIDC code flow with trusted issuer and audience");
            }
        }
        await app.StopAsync();
    }
    private static void Check(bool value,string description)
    {if(!value)throw new InvalidOperationException(description);}
}
