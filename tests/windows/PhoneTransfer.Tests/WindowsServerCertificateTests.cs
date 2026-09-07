using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PhoneTransfer.Infrastructure.Security;
using Xunit;

namespace PhoneTransfer.Tests;

public class WindowsServerCertificateTests
{
    [Fact]
    public void ReusesCurrentUserNonExportableKeyAndStoredCertificate()
    {
        if (!OperatingSystem.IsWindows()) return;
        var id = Guid.NewGuid();
        var keyName = $"PhoneTransfer.Server.{id:D}";
        try
        {
            var certificates = new WindowsServerCertificate();
            using var first = certificates.GetOrCreate(id);
            using var second = new WindowsServerCertificate().GetOrCreate(id);
            Assert.Equal(first.Thumbprint, second.Thumbprint);
            Assert.True(first.HasPrivateKey);
            using var privateKey = Assert.IsType<ECDsaCng>(first.GetECDsaPrivateKey());
            Assert.False(privateKey.Key.IsEphemeral);
            Assert.False(privateKey.Key.IsMachineKey);
            Assert.Equal(CngExportPolicies.None, privateKey.Key.ExportPolicy);
            Assert.Throws<CryptographicException>(() => first.Export(X509ContentType.Pkcs12));
            var message = RandomNumberGenerator.GetBytes(32);
            using var publicKey = second.GetECDsaPublicKey()!;
            Assert.True(publicKey.VerifyData(message, privateKey.SignData(message, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));
        }
        finally
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    if (certificate.Subject == $"CN=PhoneTransfer-{id:D}") store.Remove(certificate);
                }
            }
            if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
            {
                using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                key.Delete();
            }
        }
    }
}
