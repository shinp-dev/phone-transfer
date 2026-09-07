using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PhoneTransfer.Infrastructure.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsServerCertificate
{
    private static readonly object CreationGate = new();

    public X509Certificate2 GetOrCreate(Guid deviceId)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("A stable device identity is required.");
        lock (CreationGate)
        {
            var keyName = $"PhoneTransfer.Server.{deviceId:D}";
            using var key = CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
                ? CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider)
                : CngKey.Create(CngAlgorithm.ECDsaP256, keyName, new CngKeyCreationParameters
                {
                    Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                });
            if (key.ExportPolicy != CngExportPolicies.None || key.IsMachineKey || key.IsEphemeral)
                throw new CryptographicException("Server key does not meet the storage policy.");
            using var signingKey = new ECDsaCng(key);
            var publicKey = signingKey.ExportSubjectPublicKeyInfo();
            var subject = new X500DistinguishedName($"CN=PhoneTransfer-{deviceId:D}");
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            X509Certificate2? existing = null;
            foreach (var candidate in store.Certificates)
            {
                using var candidateKey = candidate.GetECDsaPublicKey();
                if (existing is null && candidate.SubjectName.RawData.AsSpan().SequenceEqual(subject.RawData) &&
                    candidate.HasPrivateKey && candidate.NotBefore.ToUniversalTime() <= DateTime.UtcNow &&
                    candidate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30) && candidateKey is not null &&
                    candidateKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey)) existing = candidate;
                else candidate.Dispose();
            }
            if (existing is not null) return existing;
            var request = new CertificateRequest(subject, signingKey, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1")
            }, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName($"phonetransfer-{deviceId:N}.local");
            request.CertificateExtensions.Add(names.Build());
            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            try
            {
                store.Add(certificate);
                return certificate;
            }
            catch
            {
                certificate.Dispose();
                throw;
            }
        }
    }
}
