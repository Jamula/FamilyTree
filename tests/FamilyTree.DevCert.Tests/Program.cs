using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FamilyTree.DevCert;
using DevCertProgram = FamilyTree.DevCert.Program;

var failures = 0;
void Check(bool condition, string name)
{
    if (!condition) { Console.Error.WriteLine("FAIL: " + name); failures++; }
    else Console.WriteLine("PASS: " + name);
}
void Reject(Action action, string name)
{
    try { action(); Check(false, name); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or CryptographicException)
    { Check(true, name); }
}

var hash = new string('A', 40);
Check(DevCertProgram.TryParse(["setup", "new.cer"], out _, out _, out _), "setup requires destination");
Check(!DevCertProgram.TryParse(["setup"], out _, out _, out _), "missing destination rejected");
Check(!DevCertProgram.TryParse(["setup", "new.pfx"], out _, out _, out _), "private-key extension rejected");
Check(DevCertProgram.TryParse(["renew", hash, "new.cer"], out _, out _, out _), "renew explicit inputs");
Check(!DevCertProgram.TryParse(["renew", "AA", "new.cer"], out _, out _, out _), "short thumbprint rejected");
Check(!DevCertProgram.TryParse(["status", new string('G', 40)], out _, out _, out _), "non-hex thumbprint rejected");
Check(!DevCertProgram.TryParse(["setup", "new.cer", "extra"], out _, out _, out _), "extra argument rejected");
Check(CertificatePolicy.IsDedicatedKeyName("FamilyTree.DevCert." + Guid.NewGuid().ToString("N")), "dedicated key name accepted");
Check(!CertificatePolicy.IsDedicatedKeyName("FamilyTree.DevCert.not-a-guid"), "non-dedicated key name rejected");

using var rsa = RSA.Create(2048);
var now = DateTimeOffset.UtcNow;
using var valid = CertificatePolicy.CreateRequest(rsa).CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
CertificatePolicy.ValidateCertificate(valid, now);
Check(true, "dedicated certificate metadata accepted");
var collection = new X509Certificate2Collection { valid };
Check(ReferenceEquals(CertificatePolicy.Select(collection, valid.Thumbprint.ToLowerInvariant()), valid), "exact case-insensitive selection");
Reject(() => CertificatePolicy.Select(collection, hash), "missing certificate rejected");
collection.Add(valid);
Reject(() => CertificatePolicy.Select(collection, valid.Thumbprint), "ambiguous certificate rejected");
Reject(() => CertificatePolicy.ValidateCertificate(valid, now.AddDays(2)), "expired certificate rejected");
Reject(() => CertificatePolicy.ValidateCertificate(valid, now.AddDays(-2)), "not-yet-valid certificate rejected");
using var publicOnly = X509CertificateLoader.LoadCertificate(valid.Export(X509ContentType.Cert));
Reject(() => CertificatePolicy.ValidateCertificate(publicOnly, now), "missing private key rejected");
var otherRequest = new CertificateRequest("CN=Other", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var other = otherRequest.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
Reject(() => CertificatePolicy.ValidateCertificate(other, now), "unrelated certificate rejected");

var unsupported = Invoke("status", hash);
if (!OperatingSystem.IsWindows())
{
    Check(unsupported.ExitCode != 0 && unsupported.Error.Contains("requires Windows", StringComparison.Ordinal), "non-Windows fails closed");
    Console.WriteLine("SKIP: Windows CNG/store persistence and rotation integration tests (Windows required).");
}
else
{
    var outputDirectory = Environment.GetEnvironmentVariable("FAMILYTREE_DEVCERT_TEST_OUTPUT_DIR");
    if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory) || !Directory.Exists(outputDirectory))
    {
        Console.WriteLine("SKIP: Set FAMILYTREE_DEVCERT_TEST_OUTPUT_DIR to an existing absolute output directory outside the repository to run Windows integration tests.");
    }
    else
    {
        RunWindowsTests(Check, outputDirectory);
    }
}
return failures == 0 ? 0 : 1;

static (int ExitCode, string Output, string Error) Invoke(params string[] args)
{
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(typeof(CertificatePolicy).Assembly.Location);
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start test process.");
    var stdout = process.StandardOutput.ReadToEnd();
    var stderr = process.StandardError.ReadToEnd();
    process.WaitForExit();
    return (process.ExitCode, stdout, stderr);
}

[SupportedOSPlatform("windows")]
static void RunWindowsTests(Action<bool, string> check, string directory)
{
    var created = new List<string>();
    var createdFiles = new List<string>();
    try
    {
        var firstPath = Path.Combine(directory, "FamilyTree.DevCert.Tests-" + Guid.NewGuid().ToString("N") + ".cer");
        var first = Invoke("setup", firstPath);
        check(first.ExitCode == 0, "Windows setup succeeds");
        if (first.ExitCode != 0) return;
        createdFiles.Add(firstPath);
        var firstHash = ExtractThumbprint(first.Output);
        created.Add(firstHash);
        var status = Invoke("status", firstHash);
        check(status.ExitCode == 0, "persisted key signs after setup process exits");
        var bytes = File.ReadAllBytes(firstPath);
        using var publicCert = X509CertificateLoader.LoadCertificate(bytes);
        check(!publicCert.HasPrivateKey && publicCert.Thumbprint == firstHash, "export is public certificate only");
        var collision = Invoke("setup", firstPath);
        check(collision.ExitCode != 0 && File.ReadAllBytes(firstPath).SequenceEqual(bytes), "existing file is not overwritten");
        var secondPath = Path.Combine(directory, "FamilyTree.DevCert.Tests-" + Guid.NewGuid().ToString("N") + ".cer");
        var renewed = Invoke("renew", firstHash, secondPath);
        check(renewed.ExitCode == 0, "renew succeeds");
        if (renewed.ExitCode != 0) return;
        createdFiles.Add(secondPath);
        var secondHash = ExtractThumbprint(renewed.Output);
        created.Add(secondHash);
        check(firstHash != secondHash && Invoke("status", firstHash).ExitCode == 0 &&
              Invoke("status", secondHash).ExitCode == 0, "renew retains old key and creates new persisted key");
        CheckRejectedKey(check, CngExportPolicies.AllowExport, "FamilyTree.DevCert." + Guid.NewGuid().ToString("N"),
            "exportable key rejected");
        CheckRejectedKey(check, CngExportPolicies.None, "Unrelated." + Guid.NewGuid().ToString("N"),
            "non-dedicated key rejected");
    }
    finally
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        var certs = store.Certificates;
        try
        {
            foreach (var cert in certs)
            {
                if (!created.Contains(cert.Thumbprint, StringComparer.OrdinalIgnoreCase)) continue;
                using var rsa = cert.GetRSAPrivateKey() as RSACng;
                store.Remove(cert);
                rsa?.Key.Delete();
            }
        }
        finally { foreach (var cert in certs) cert.Dispose(); }
        foreach (var file in createdFiles) File.Delete(file);
    }
}

[SupportedOSPlatform("windows")]
static void CheckRejectedKey(Action<bool, string> check, CngExportPolicies policy, string name, string test)
{
    using var key = CngKey.Create(CngAlgorithm.Rsa, name, new CngKeyCreationParameters
    {
        Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
        ExportPolicy = policy,
        KeyUsage = CngKeyUsages.Signing,
        Parameters = { new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None) }
    });
    try
    {
        using var rsa = new RSACng(key);
        var now = DateTimeOffset.UtcNow;
        using var cert = CertificatePolicy.CreateRequest(rsa).CreateSelfSigned(now.AddMinutes(-1), now.AddDays(1));
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        store.Add(cert);
        try { check(Invoke("status", cert.Thumbprint).ExitCode != 0, test); }
        finally { store.Remove(cert); }
    }
    finally { key.Delete(); }
}

static string ExtractThumbprint(string output) =>
    output.Split('\n').Single(line => line.StartsWith("Thumbprint: ", StringComparison.Ordinal))[12..].Trim();
