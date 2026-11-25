using FamilyTree.Web.Components;
using FamilyTree.Web;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "FamilyTreeWebAntiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.Path = "/";
});
builder.Services.AddSingleton<SyntheticReviewWorkbench>();
var privateTreeMode = PrivateTreeModeOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(privateTreeMode);
builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false
})
{
    Timeout = TimeSpan.FromSeconds(5)
});
builder.Services.AddSingleton<PrivateTreeApiClient>();
var app = builder.Build();
app.MapGet("/", () => new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<App>());
app.MapGet("/demo", (Delegate)(Func<HttpContext, Task<IResult>>)SyntheticTreeExplorer.ShowAsync);
PrivateTreeRoutes.Map(app);
SyntheticWorkbenchRoutes.Map(app);
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "FamilyTree.Web" }));
app.Run();
