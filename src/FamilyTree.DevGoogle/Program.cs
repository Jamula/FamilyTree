using System.Runtime.Versioning;

namespace FamilyTree.DevGoogle;

internal static class Program
{
    private const string Usage = """
        FamilyTree Google developer launcher (Windows only)
          setup   Configure credentials if not already present.
          update  Replace both credentials.
          status  Check whether credentials are present.
          remove  Remove credentials.
          start   Start the sibling FamilyTree.Api package on https://localhost:5182.

        Publish the API to a sibling directory named FamilyTree.Api and this
        launcher to FamilyTree.DevGoogle. Install the .NET runtime and a trusted
        ASP.NET Core HTTPS development certificate for the current user.
        Register https://localhost:5182/auth/callback/google with Google.
        Input requires an interactive console; do not record the screen or use
        a terminal that records keystrokes. Credentials are stored per user in
        Windows Credential Manager. Sign-in does not grant family access.
        """;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        if (args.Length != 1 || args[0] is not ("setup" or "update" or "status" or "remove" or "start"))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Windows Credential Manager and Windows are required.");
            return 1;
        }
        try
        {
            return await RunWindows(args[0]);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Operation failed. No details were logged; check the credential store, API package, runtime and HTTPS development certificate.");
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunWindows(string command)
    {
        var store = new WindowsCredentialStore();
        if (command == "remove")
        {
            store.Remove();
            Console.WriteLine("Credentials removed or already absent.");
            return 0;
        }
        var current = store.Read();
        if (command == "status" || (command == "setup" && current is not null))
        {
            Console.WriteLine(current is null ? "Credentials are not configured." : "Credentials are configured.");
            return current is null ? 1 : 0;
        }
        if (command is "setup" or "update")
        {
            if (Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected)
                throw new InvalidOperationException();
            var id = ReadHidden("Google client ID: ");
            var secret = ReadHidden("Google client secret: ");
            store.Write(new Credentials(id, secret));
            Console.WriteLine("Credentials saved.");
            return 0;
        }
        if (current is null)
        {
            Console.Error.WriteLine("Credentials are not configured.");
            return 1;
        }
        return await ApiLauncher.RunAsync(current);
    }

    private static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var value = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Escape) throw new OperationCanceledException();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0) value.Length--;
            }
            else if (!char.IsControl(key.KeyChar) && value.Length < 1000)
                value.Append(key.KeyChar);
            else
                throw new InvalidOperationException();
        }
        Console.WriteLine();
        var result = value.ToString();
        if (string.IsNullOrWhiteSpace(result) || result != result.Trim())
            throw new InvalidOperationException();
        return result;
    }
}
