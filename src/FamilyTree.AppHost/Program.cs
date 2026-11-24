using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

// Local-only, no containers or billable cloud resources.
// Service discovery is limited to this development application.
var api = builder.AddDotnetProject("api", "../FamilyTree.Api/FamilyTree.Api.csproj")
    .WithHttpEndpoint(port: 5181, name: "http")
    .WithHttpsEndpoint(port: 5183, name: "https")
    .WithHttpHealthCheck("/health")
    .WithEnvironment("FAMILYTREE_OTLP_ENABLED", "true");

foreach (var setting in new[]
{
    "FAMILYTREE_PRIVATE_API_ENABLED",
    "FAMILYTREE_PRIVATE_API_DATA_DIRECTORY",
    "FAMILYTREE_PRIVATE_API_ENCRYPTION_KEY",
    "FAMILYTREE_PRIVATE_API_IDENTITY_KEY",
    "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE",
    "FAMILYTREE_PRIVATE_API_DATA_PROTECTION_CERTIFICATE_PASSWORD",
    "FAMILYTREE_OIDC_GOOGLE_CLIENT_ID",
    "FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET",
    "FAMILYTREE_OIDC_MICROSOFT_TENANT_ID",
    "FAMILYTREE_OIDC_MICROSOFT_CLIENT_ID",
    "FAMILYTREE_OIDC_MICROSOFT_CLIENT_SECRET"
})
{
    if (builder.Configuration[setting] is { } value)
        api.WithEnvironment(setting, value);
}

builder.AddDotnetProject("web", "../FamilyTree.Web/FamilyTree.Web.csproj")
    .WithHttpEndpoint(port: 5182, name: "http")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(api)
    .WaitFor(api);

builder.Build().Run();
