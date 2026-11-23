using System.Runtime.CompilerServices;
using FamilyTree.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

internal static class InteractiveOidcEndpointAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        var cookieOptions = new CookieAuthenticationOptions();
        InteractiveOidc.ConfigureCookieOptions(cookieOptions);
        Check(cookieOptions.Cookie.Name=="__Host-FamilyTreeSession" &&
              cookieOptions.Cookie.HttpOnly &&
              cookieOptions.Cookie.SecurePolicy==CookieSecurePolicy.Always &&
              cookieOptions.Cookie.SameSite==SameSiteMode.Lax &&
              !cookieOptions.SlidingExpiration &&
              cookieOptions.ExpireTimeSpan==TimeSpan.FromHours(8),
              "secure login cookie settings");
        var scheme=new AuthenticationScheme("Cookies","Cookies",typeof(CookieAuthenticationHandler));
        var loginContext=new RedirectContext<CookieAuthenticationOptions>(
            new DefaultHttpContext(),scheme,cookieOptions,new AuthenticationProperties(),"/auth/login");
        cookieOptions.Events.OnRedirectToLogin(loginContext).GetAwaiter().GetResult();
        Check(loginContext.Response.StatusCode==StatusCodes.Status401Unauthorized,
            "anonymous access returns 401 rather than redirect");
        var deniedContext=new RedirectContext<CookieAuthenticationOptions>(
            new DefaultHttpContext(),scheme,cookieOptions,new AuthenticationProperties(),"/auth/denied");
        cookieOptions.Events.OnRedirectToAccessDenied(deniedContext).GetAwaiter().GetResult();
        Check(deniedContext.Response.StatusCode==StatusCodes.Status403Forbidden,
            "forbidden access returns 403 rather than redirect");

        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"]="synthetic-google",
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"]="synthetic-secret",
            ["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"]=Guid.NewGuid().ToString("D"),
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"]="synthetic-microsoft",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"]="synthetic-secret"
        }).Build();
        var providers=InteractiveOidc.ReadConfiguration(config);
        Check(providers.Count==2,"Google and Microsoft configured only explicitly");
        foreach(var provider in providers)
        {
            var options=new OpenIdConnectOptions();
            InteractiveOidc.ConfigureProviderOptions(options,provider);
            Check(options.RequireHttpsMetadata && options.UsePkce &&
                  options.ResponseType=="code" && !options.SaveTokens &&
                  options.TokenValidationParameters.ValidateIssuer &&
                  options.TokenValidationParameters.ValidateAudience &&
                  options.CallbackPath==provider.Callback &&
                  options.ClientId==provider.ClientId &&
                  options.SignInScheme==CookieAuthenticationDefaults.AuthenticationScheme,
                  "provider code+PKCE, HTTPS and exact issuer/audience");
        }
        Expect<ArgumentNullException>(()=>InteractiveOidc.ConfigureCookieOptions(null!),
            "null cookie config rejected");
        Expect<ArgumentNullException>(()=>InteractiveOidc.ConfigureProviderOptions(
            null!, providers[0]),"null OIDC options rejected");
        Expect<ArgumentNullException>(()=>InteractiveOidc.ConfigureProviderOptions(
            new OpenIdConnectOptions(), null!),"null provider rejected");
        Console.WriteLine("PASS OIDC cookie 401/403, PKCE config and negative tests");
    }
    static void Check(bool condition,string why)
    {if(!condition)throw new InvalidOperationException(why);}
    static void Expect<T>(Action action,string why) where T:Exception
    {var caught=false;try{action();}catch(T){caught=true;}Check(caught,why);}
}
