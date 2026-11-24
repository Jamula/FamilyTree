using FamilyTree.Web.Components;
using FamilyTree.Web;
using FamilyTree.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddFamilyTreeObservability(builder.Configuration);
builder.Services.AddRazorComponents();
builder.Services.AddAntiforgery();
builder.Services.AddSingleton<SyntheticReviewWorkbench>();
var app = builder.Build();
app.UseFamilyTreeRequestTelemetry();
app.MapGet("/", () => new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<App>());
app.MapGet("/demo", (Delegate)(Func<HttpContext, Task<IResult>>)SyntheticTreeExplorer.ShowAsync);
SyntheticWorkbenchRoutes.Map(app);
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "FamilyTree.Web" }));
app.Run();
