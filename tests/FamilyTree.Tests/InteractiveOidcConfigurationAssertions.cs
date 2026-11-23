using System.Runtime.CompilerServices;
using FamilyTree.Api;
using Microsoft.Extensions.Configuration;

internal static class InteractiveOidcConfigurationAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        IConfiguration Empty() => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string,string?>()).Build();
        IConfiguration Config(Dictionary<string,string?> vars) =>
            new ConfigurationBuilder().AddInMemoryCollection(vars).Build();

        Check(InteractiveOidc.ReadConfiguration(Empty()).Count==0,
            "no OIDC provider enabled without runtime secrets");
        var google=InteractiveOidc.ReadConfiguration(Config(new(){
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"]="synthetic-client",
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"]="synthetic-secret"
        }));
        Check(google.Count==1 && google[0].Name=="google" &&
            google[0].Authority=="https://accounts.google.com",
            "Google OIDC fixed issuer");
        Reject(()=>InteractiveOidc.ReadConfiguration(Config(new(){
            ["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"]="partial-only"
        })),"partially configured Google rejected");

        var tenant=Guid.NewGuid();
        var microsoft=InteractiveOidc.ReadConfiguration(Config(new(){
            ["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"]=tenant.ToString("D"),
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"]="synthetic-client",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"]="synthetic-secret"
        }));
        Check(microsoft.Count==1 &&
            microsoft[0].Authority.EndsWith(tenant.ToString("D")+"/v2.0",
                StringComparison.Ordinal),"tenant-pinned Microsoft issuer");
        Reject(()=>InteractiveOidc.ReadConfiguration(Config(new(){
            ["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"]="common",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID"]="synthetic",
            ["FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"]="synthetic"
        })),"unrestricted Microsoft tenant rejected");
        Reject(()=>InteractiveOidc.ReadConfiguration(Config(new(){
            ["FAMILYTREE_OIDC_MICROSOFT_TENANT_ID"]=tenant.ToString("D")
        })),"partially configured Microsoft rejected");
        Check(google[0].Callback=="/auth/callback/google" &&
            microsoft[0].Callback=="/auth/callback/microsoft",
            "provider callbacks are deterministic");
        Console.WriteLine("PASS OIDC provider default-off and malformed config regression");
    }
    static void Check(bool good,string why)
    {if(!good)throw new InvalidOperationException(why);}
    static void Reject(Action action,string why)
    {
        var rejected=false;
        try{action();}catch(InvalidOperationException){rejected=true;}
        Check(rejected,why);
    }
}
