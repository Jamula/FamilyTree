using FamilyTree.Domain;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "FamilyTree.Api" }))
   .WithName("Health");
app.MapGet("/api/v1/capabilities", () => Results.Ok(new {
    genealogyExchange = "GEDCOM 7 (planned adapter)",
    treeDefaults = new TreeOptions(),
    authorization = "not yet configured",
    publicFamilyRecords = false
})).WithName("Capabilities");

// Fail closed: do not expose records before OIDC and per-record authorization exist.
app.MapGet("/api/v1/people", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
app.MapGet("/api/v1/tree", () => Results.StatusCode(StatusCodes.Status501NotImplemented));
app.Run();
