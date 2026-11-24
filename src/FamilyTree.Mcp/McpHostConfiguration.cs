using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace FamilyTree.Mcp;

/// <summary>
/// Real stdio-only registration shared by production and tests. Register only
/// the explicitly reviewed synthetic tool type; do not discover tools assembly-wide.
/// Does not create a listener or grant approval/persistence authority.
/// </summary>
public static class McpHostConfiguration
{
    public static HostApplicationBuilder Configure(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        // MCP protocol messages must be the only stdout content.
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithTools(new[] { typeof(FamilyDemoTools) });
        return builder;
    }
}
