using System.Runtime.CompilerServices;
using FamilyTree.Mcp;
internal static class McpInputAssertions
{
    [ModuleInitializer]
    internal static void Run()
    {
        Deny(() => DemoProposalQueue.Stage("arbitrary", "valid"), "unknown kind");
        Deny(() => DemoProposalQueue.Stage("person", ""), "blank draft");
        Deny(() => DemoProposalQueue.Stage("person", new string('x', 257)),
            "oversize draft");
        Deny(() => DemoProposalQueue.Stage("person", "line\ncontrol"),
            "control character");
        Console.WriteLine("PASS synthetic MCP draft input bounds");
    }
    private static void Deny(Action action, string reason)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception(reason);
    }
}