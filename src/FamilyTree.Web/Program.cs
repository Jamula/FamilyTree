using FamilyTree.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents();
var app = builder.Build();
app.MapGet("/", () => new Microsoft.AspNetCore.Http.HttpResults.RazorComponentResult<App>());
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "FamilyTree.Web" }));
app.Run();
