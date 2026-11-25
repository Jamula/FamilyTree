using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace FamilyTree.DevCert;

internal static class CertificatePolicy
{
    internal const string Subject = "CN=FamilyTree Developer Signing";
    internal const string MarkerOid = "1.3.6.1.4.1.57264.126.1";
    private static readonly byte[] Marker = [0x05, 0x00];

    internal static CertificateRequest CreateRequest(RSA rsa)
    {
        var request = new CertificateRequest(Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509Extension(new Oid(MarkerOid), Marker, false));
        return request;
    }

    internal static X509Certificate2 Select(X509Certificate2Collection certificates, string thumbprint)
    {
        if (!Program.IsThumbprint(thumbprint)) throw new ArgumentException("Invalid thumbprint.");
        var matches = certificates.Cast<X509Certificate2>()
            .Where(c => string.Equals(c.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : throw new InvalidOperationException("Certificate selection failed.");
    }

    internal static void ValidateCertificate(X509Certificate2 cert, DateTimeOffset now)
    {
        var marker = cert.Extensions.Cast<X509Extension>().Where(e => e.Oid?.Value == MarkerOid).ToArray();
        var usage = cert.Extensions.OfType<X509KeyUsageExtension>().ToArray();
        var constraints = cert.Extensions.OfType<X509BasicConstraintsExtension>().ToArray();
        if (cert.Subject != Subject || marker.Length != 1 || !marker[0].RawData.AsSpan().SequenceEqual(Marker) ||
            usage.Length != 1 || usage[0].KeyUsages != X509KeyUsageFlags.DigitalSignature ||
            constraints.Length != 1 || constraints[0].CertificateAuthority ||
            now < cert.NotBefore.ToUniversalTime() || now >= cert.NotAfter.ToUniversalTime() || !cert.HasPrivateKey)
            throw new InvalidOperationException("Certificate is not a valid dedicated signing certificate.");
    }

    [SupportedOSPlatform("windows")]
    internal static void ValidateWindows(X509Certificate2 cert, DateTimeOffset now)
    {
        ValidateCertificate(cert, now);
        using var privateKey = cert.GetRSAPrivateKey();
        using var publicKey = cert.GetRSAPublicKey();
        if (privateKey is not RSACng rsa || publicKey is null || rsa.KeySize < 2048 ||
            rsa.Key.IsEphemeral || rsa.Key.IsMachineKey ||
            rsa.Key.Provider?.Provider != CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider ||
            !IsDedicatedKeyName(rsa.Key.KeyName) ||
            rsa.Key.ExportPolicy != CngExportPolicies.None ||
            (rsa.Key.KeyUsage & CngKeyUsages.Signing) == 0)
            throw new InvalidOperationException("Private key is not a persisted nonexportable signing key.");

        var challenge = RandomNumberGenerator.GetBytes(32);
        var signature = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!publicKey.VerifyData(challenge, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            throw new CryptographicException("Signing verification failed.");
    }

    internal static bool IsDedicatedKeyName(string? name)
    {
        const string prefix = "FamilyTree.DevCert.";
        return name is not null && name.StartsWith(prefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(name[prefix.Length..], "N", out _);
    }
}
