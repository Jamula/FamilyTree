using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;

namespace FamilyTree.DevCert;

internal static class Program
{
    private const string Usage = """
        FamilyTree developer signing certificate (Windows CurrentUser/My only)
          setup <new-public-certificate.cer>
          status <exact-40-hex-thumbprint>
          renew <exact-40-hex-thumbprint> <new-public-certificate.cer>

        Specify a new destination explicitly; existing files are never overwritten.
        Renew keeps the old certificate. Upload the public .cer manually if needed.
        Microsoft login and Graph registration are not integrated by this utility.
        """;

    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        if (!TryParse(args, out var command, out var thumbprint, out var destination))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("This utility requires Windows and the current user's certificate store.");
            return 1;
        }

        try
        {
            return RunWindows(command, thumbprint, destination);
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Certificate operation failed. No exception details are logged. A newly created public output file may remain; inspect it before retrying.");
            return 1;
        }
    }

    internal static bool TryParse(string[] args, out string command, out string? thumbprint, out string? destination)
    {
        command = args.Length > 0 ? args[0] : "";
        thumbprint = null;
        destination = null;
        if (command == "setup" && args.Length == 2)
            destination = args[1];
        else if (command == "status" && args.Length == 2)
            thumbprint = args[1];
        else if (command == "renew" && args.Length == 3)
        {
            thumbprint = args[1];
            destination = args[2];
        }
        else
            return false;

        return (thumbprint is null || IsThumbprint(thumbprint)) &&
               (destination is null || (!string.IsNullOrWhiteSpace(destination) &&
                                        destination.EndsWith(".cer", StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool IsThumbprint(string value) =>
        value.Length == 40 && value.All(c => char.IsAsciiHexDigit(c));

    [SupportedOSPlatform("windows")]
    private static int RunWindows(string command, string? thumbprint, string? destination)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open((command == "status" ? OpenFlags.ReadOnly : OpenFlags.ReadWrite) | OpenFlags.OpenExistingOnly);
        if (thumbprint is not null)
        {
            var certificates = store.Certificates;
            try
            {
                var cert = CertificatePolicy.Select(certificates, thumbprint);
                CertificatePolicy.ValidateWindows(cert, DateTimeOffset.UtcNow);
                if (command == "status")
                {
                    PrintStatus(cert);
                    return 0;
                }
            }
            finally
            {
                foreach (var cert in certificates) cert.Dispose();
            }
        }

        // CreateNew never follows an existing leaf symlink or overwrites a file.
        using var output = new FileStream(destination!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var key = CngKey.Create(CngAlgorithm.Rsa, "FamilyTree.DevCert." + Guid.NewGuid().ToString("N"),
            new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.Signing,
                Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) }
            });
        var stored = false;
        try
        {
            using var rsa = new RSACng(key);
            var request = CertificatePolicy.CreateRequest(rsa);
            var now = DateTimeOffset.UtcNow;
            using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(1));
            CertificatePolicy.ValidateWindows(cert, now);
            var publicBytes = cert.Export(X509ContentType.Cert);
            output.Write(publicBytes);
            output.Flush(flushToDisk: true);
            store.Add(cert);
            stored = true;
            PrintStatus(cert);
            Console.WriteLine("Public certificate exported. No registration or login was performed; retain the old certificate during rotation.");
            return 0;
        }
        finally
        {
            if (!stored) key.Delete();
        }
    }

    private static void PrintStatus(X509Certificate2 cert)
    {
        Console.WriteLine($"Thumbprint: {cert.Thumbprint}");
        Console.WriteLine($"Expires (UTC): {cert.NotAfter.ToUniversalTime():O}");
        if (cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))
            Console.WriteLine("Warning: certificate expires within 30 days.");
    }
}
