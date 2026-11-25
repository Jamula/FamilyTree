using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace FamilyTree.Mcp;

/// <summary>
/// Real stdio-only registration shared by production and tests. Reject any
/// unreviewed tool type before registering the synthetic demo tools.
/// Does not create a listener or grant approval/persistence authority.
/// </summary>
public static class McpHostConfiguration
{
    public static HostApplicationBuilder Configure(HostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var toolTypes = typeof(FamilyDemoTools).Assembly.GetTypes()
            .Where(type => Attribute.IsDefined(type, typeof(McpServerToolTypeAttribute)))
            .ToArray();
        var reviewedMethods = new HashSet<MethodInfo>
        {
            ReviewedMethod(nameof(FamilyDemoTools.FamilyOverview)),
            ReviewedMethod(nameof(FamilyDemoTools.ListDemoPeople), typeof(int)),
            ReviewedMethod(nameof(FamilyDemoTools.SearchDemoPeople), typeof(string)),
            ReviewedMethod(nameof(FamilyDemoTools.InspectDemoPerson), typeof(string)),
            ReviewedMethod(nameof(FamilyDemoTools.ListDemoRelationships), typeof(int)),
            ReviewedMethod(nameof(FamilyDemoTools.ProposeDemoPerson), typeof(string)),
            ReviewedMethod(nameof(FamilyDemoTools.ProposeDemoRelationship),
                typeof(string), typeof(string), typeof(string)),
            ReviewedMethod(nameof(FamilyDemoTools.ListDemoDrafts))
        };
        var registeredMethods = typeof(FamilyDemoTools).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => Attribute.IsDefined(method, typeof(McpServerToolAttribute)))
            .ToHashSet();
        if (toolTypes.Length != 1 || toolTypes[0] != typeof(FamilyDemoTools) ||
            !registeredMethods.SetEquals(reviewedMethods))
            throw new InvalidOperationException("An unreviewed MCP tool type is present.");

        // MCP protocol messages must be the only stdout content.
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        builder.Services.AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly(typeof(FamilyDemoTools).Assembly);
        return builder;
    }

    private static MethodInfo ReviewedMethod(string name, params Type[] parameterTypes) =>
        typeof(FamilyDemoTools).GetMethod(name,
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            binder: null, types: parameterTypes, modifiers: null) ??
        throw new InvalidOperationException("A reviewed MCP tool method is missing.");
}
