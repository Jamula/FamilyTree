using FamilyTree.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

internal static class McpHostRegistrationAssertions
{
    internal static void Run()
    {
        var rejected = false;
        try { McpHostConfiguration.Configure(null!); }
        catch (ArgumentNullException) { rejected = true; }
        Check(rejected, "null MCP host builder cannot register a transport");

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        var before = builder.Services.Count;
        var returned = McpHostConfiguration.Configure(builder);
        Check(ReferenceEquals(returned, builder) && builder.Services.Count > before,
            "production MCP stdio services registered on the original host");
        Check(builder.Services.Any(item =>
            item.ServiceType.FullName?.Contains("ModelContextProtocol",
                StringComparison.Ordinal) == true),
            "MCP server registered rather than a web listener");

        // Constructing a host (without starting it) must not open a port,
        // consume stdin, launch a remote provider or persist any records.
        using var host = builder.Build();
        var options = host.Services.GetRequiredService<IOptionsMonitor<ConsoleLoggerOptions>>()
            .CurrentValue;
        Check(options.LogToStandardErrorThreshold == LogLevel.Trace,
            "MCP console diagnostics must not corrupt stdout protocol messages");
        Console.WriteLine("PASS MCP stdio host setup, logger isolation and null builder handling");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
