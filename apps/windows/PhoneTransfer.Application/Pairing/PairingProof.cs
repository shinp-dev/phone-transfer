using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Application.Pairing;

public sealed record VerifiedPairingIdentity(Guid DeviceId, string DisplayName, string CertificateSha256);

public static class PairingProof
{
    public static VerifiedPairingIdentity? Verify(PairingRequest request, DateTimeOffset now)
    {
        if (!Guid.TryParseExact(request.DeviceId, "D", out var deviceId) || deviceId == Guid.Empty ||
            request.DeviceId != deviceId.ToString("D") ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 128 ||
            request.DisplayName.Any(char.IsControl) || !request.DisplayName.IsNormalized() ||
            request.Token is null || request.Token.Length != 43 ||
            request.Token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) ||
            request.CertificateDer is null || request.CertificateDer.Length is < 1 or > 8192 ||
            request.ProofSignature is null || request.ProofSignature.Length is < 1 or > 2048) return null;
        try
        {
            var der = Convert.FromBase64String(request.CertificateDer);
            var signature = Convert.FromBase64String(request.ProofSignature);
            if (Convert.ToBase64String(der) != request.CertificateDer ||
                Convert.ToBase64String(signature) != request.ProofSignature) return null;
            using var certificate = X509CertificateLoader.LoadCertificate(der);
            if (!der.AsSpan().SequenceEqual(certificate.RawData) ||
                now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime() ||
                !certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)) return null;
            var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            var enhanced = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            if (constraints is null || constraints.CertificateAuthority || usage is null ||
                !usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature) ||
                usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign) || enhanced is null ||
                !enhanced.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.2")) return null;
            using var key = certificate.GetECDsaPublicKey();
            if (key is null || key.KeySize != 256 ||
                key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7") return null;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.VerificationTime = now.UtcDateTime;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            if (!chain.Build(certificate)) return null;
            var fingerprint = Convert.ToHexStringLower(SHA256.HashData(der));
            var payload = Encoding.UTF8.GetBytes(Transcript(request.DeviceId, request.DisplayName,
                request.Token, fingerprint));
            return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
                ? new VerifiedPairingIdentity(deviceId, request.DisplayName, fingerprint)
                : null;
        }
        catch (FormatException) { return null; }
        catch (CryptographicException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public static string Transcript(string deviceId, string displayName, string token, string certificateSha256) =>
        $"phone-transfer/pairing/v1\n{deviceId}\n{displayName}\n{token}\n{certificateSha256}";
}
