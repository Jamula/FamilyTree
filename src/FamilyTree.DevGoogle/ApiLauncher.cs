using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace FamilyTree.DevGoogle;

internal static class ApiLauncher
{
    internal static ProcessStartInfo CreateStartInfo(string dotnet, string api, string contentRoot,
        string windows, string profile, string roaming, string local, Credentials credentials)
    {
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = contentRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(api);
        start.ArgumentList.Add("--urls");
        start.ArgumentList.Add("https://localhost:5182");
        start.ArgumentList.Add("--contentRoot");
        start.ArgumentList.Add(contentRoot);

        // Stronger than the acceptance harness filter: inherit no configuration,
        // including host-prefixed variants, logging, OIDC, OTEL or Kestrel keys.
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["PATH"] = Path.Combine(windows, "System32");
        start.Environment["USERPROFILE"] = profile;
        start.Environment["APPDATA"] = roaming;
        start.Environment["LOCALAPPDATA"] = local;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["Logging__LogLevel__Default"] = "None";
        start.Environment["FAMILYTREE_OTLP_ENABLED"] = "false";
        start.Environment["AllowedHosts"] = "localhost";
        start.Environment["FAMILYTREE_OIDC_GOOGLE_CLIENT_ID"] = credentials.ClientId;
        start.Environment["FAMILYTREE_OIDC_GOOGLE_CLIENT_SECRET"] = credentials.ClientSecret;
        return start;
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<int> RunAsync(Credentials credentials)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (new[] { windows, profile, roaming, local, programFiles }.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException();
        var dotnet = Path.Combine(programFiles, "dotnet", "dotnet.exe");
        var api = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "FamilyTree.Api", "FamilyTree.Api.dll"));
        if (!File.Exists(dotnet) || !File.Exists(api)) throw new InvalidOperationException();

        var directory = Path.Combine(local, "FamilyTree.DevGoogle-" + Guid.NewGuid().ToString("N"));
        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(identity);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        CreatePrivateDirectory(directory, security);
        try
        {
            var start = CreateStartInfo(dotnet, api, directory, windows, profile, roaming, local, credentials);
            using var process = Process.Start(start) ?? throw new InvalidOperationException();
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            using var stop = new CancellationTokenSource();
            ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; stop.Cancel(); };
            Console.CancelKeyPress += handler;
            try
            {
                Console.WriteLine("Starting local API at https://localhost:5182. Press Ctrl+C to stop.");
                try { await process.WaitForExitAsync(stop.Token); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                    return 0;
                }
                await Task.WhenAll(stdout, stderr);
                Console.Error.WriteLine("API stopped. Check the package and HTTPS development certificate if startup failed.");
                return process.ExitCode == 0 ? 0 : 1;
            }
            finally
            {
                Console.CancelKeyPress -= handler;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
                await Task.WhenAll(stdout, stderr);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [SupportedOSPlatform("windows")]
    private static void CreatePrivateDirectory(string path, DirectorySecurity security)
    {
        var descriptor = security.GetSecurityDescriptorBinaryForm();
        var memory = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, memory, descriptor.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = memory
            };
            // CreateDirectoryW fails rather than accepting an existing directory.
            if (!CreateDirectory(path, ref attributes)) throw new InvalidOperationException();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, ref SecurityAttributes attributes);
}
