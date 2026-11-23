using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Local-only, no containers or billable cloud resources.
// Service discovery is limited to this development application.
var api = builder.AddDotnetProject("api", "../FamilyTree.Api/FamilyTree.Api.csproj")
    .WithHttpEndpoint(port: 5181, name: "http")
    .WithEnvironment("FAMILYTREE_OTLP_ENABLED", "true");

builder.AddDotnetProject("web", "../FamilyTree.Web/FamilyTree.Web.csproj")
    .WithHttpEndpoint(port: 5182, name: "http")
    .WithExternalHttpEndpoints()
    .WithReference(api);

builder.Build().Run();
