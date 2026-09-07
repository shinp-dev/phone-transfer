using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Tests;

internal static class PairingTestCertificates
{
    public static X509Certificate2 Create(bool server = false)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(server ? "CN=server" : "CN=phone", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")
        }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
            OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
    }

    public static PairingRequest Request(X509Certificate2 certificate, string token, Guid? deviceId = null)
    {
        var id = (deviceId ?? Guid.NewGuid()).ToString("D");
        var digest = Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));
        var payload = PairingProof.Transcript(id, "Test phone", token, digest);
        return new PairingRequest(id, "Test phone", token, Convert.ToBase64String(certificate.RawData), Sign(certificate, payload));
    }

    public static string StatusProof(X509Certificate2 certificate, Guid requestId) =>
        Sign(certificate, PairingCoordinator.StatusTranscript(requestId));

    private static string Sign(X509Certificate2 certificate, string payload)
    {
        using var key = certificate.GetECDsaPrivateKey()!;
        return Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(payload), HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
    }
}

internal sealed class PairingTestClock : TimeProvider
{
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow;
    private long ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref ticks);
    public override DateTimeOffset GetUtcNow() => start.AddTicks(GetTimestamp());
    public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}
