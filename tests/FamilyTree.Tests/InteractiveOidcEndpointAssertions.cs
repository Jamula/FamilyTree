using System.Runtime.CompilerServices;
using FamilyTree.Api;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static class InteractiveOidcEndpointAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        Exercise(new Dictionary<string,string?>(), configured:false);
        Exercise(new Dictionary<string,string?>
        {
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"]="synthetic-client",
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"]="synthetic-secret",
            ["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"]=Guid.NewGuid().ToString("D"),
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"]="synthetic-ms-client",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"]="synthetic-ms-secret"
        }, configured:true);
        Console.WriteLine("PASS OIDC provider registrations and secure cookie options");
    }

    private static void Exercise(IDictionary<string,string?> vars,bool configured)
    {
        var builder=WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName="Development"
        });
        builder.Configuration.AddInMemoryCollection(vars);
        var providers=InteractiveOidc.Configure(builder);
        Check(providers.Count==(configured?2:0),"default-off provider registration");
        using var app=builder.Build();
        app.MapInteractiveOidc(providers);

        var cookies=app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        Check(cookies.Cookie.Name=="__Host-FamilyTreeSession" &&
              cookies.Cookie.HttpOnly &&
              cookies.Cookie.SecurePolicy==Microsoft.AspNetCore.Http.CookieSecurePolicy.Always &&
              cookies.Cookie.SameSite==Microsoft.AspNetCore.Http.SameSiteMode.Lax &&
              !cookies.SlidingExpiration &&
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
                      options.ClientId==provider.ClientId &&
                      options.SignInScheme==CookieAuthenticationDefaults.AuthenticationScheme,
                      "exact OIDC code flow and tenant-pinned issuer");
            }
        }
    }
    private static void Check(bool value,string description)
    {if(!value)throw new InvalidOperationException(description);}
}
