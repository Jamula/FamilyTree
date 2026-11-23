using FamilyTree.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

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
        // SDK v2.4.0 registers stdio as exactly one singleton ITransport
        // factory, and a single-session hosted service. Do not resolve the
        // factory here: constructing StdioServerTransport in-process may
        // open a blocking reader on the test process's stdin.
        var transports = builder.Services.Where(item =>
            item.ServiceType == typeof(ITransport)).ToArray();
        Check(transports.Length == 1 &&
              transports[0].Lifetime == ServiceLifetime.Singleton &&
              transports[0].ImplementationFactory?.Method.Name.Contains(
                  "WithStdioServerTransport", StringComparison.Ordinal) == true,
            "exactly one stdio transport factory, not HTTP/stream, must be registered");
        var hosted = builder.Services.Where(item =>
            item.ServiceType == typeof(IHostedService)).ToArray();
        Check(hosted.Length == 1 && hosted[0].ImplementationType?.Name ==
              "SingleSessionMcpServerHostedService",
            "stdio MCP must register exactly one approved hosted service");
        Check(!hosted.Any(item =>
                item.ImplementationType?.FullName?.Contains("WebHost",
                    StringComparison.OrdinalIgnoreCase) == true) &&
              !builder.Services.Any(item =>
                item.ServiceType.FullName ==
                    "Microsoft.AspNetCore.Hosting.Server.IServer"),
            "HTTP web-host and Kestrel listeners must not be registered");

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
