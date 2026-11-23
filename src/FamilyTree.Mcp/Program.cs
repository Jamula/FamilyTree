using FamilyTree.Mcp;
using Microsoft.Extensions.Hosting;

// Local stdio only. No HTTP listener, cloud account or private data access.
await McpHostConfiguration.Configure(Host.CreateApplicationBuilder(args))
    .Build().RunAsync();
