using System.Diagnostics;

internal static class RecoverySuiteAssertions
{
    internal static async Task RunAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null &&
               !File.Exists(Path.Combine(root.FullName, "tests", "FamilyTree.RecoveryTests",
                   "FamilyTree.RecoveryTests.csproj")))
            root = root.Parent;
        if (root is null)
            throw new DirectoryNotFoundException("Recovery test project not found.");

        var project = Path.Combine(root.FullName, "tests", "FamilyTree.RecoveryTests",
            "FamilyTree.RecoveryTests.csproj");
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--project");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--no-build");
        start.ArgumentList.Add("--no-restore");
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add("Release");

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Recovery test process could not start.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Recovery assertions failed (exit code {process.ExitCode}).{Environment.NewLine}{output}{error}");
        Console.Write(output);
    }
}
