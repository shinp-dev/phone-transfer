using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Protocol;
using Xunit;

namespace PhoneTransfer.Tests;

public class PairingProofTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T00:00:00Z");

    private static PairingRequest Request(bool clientUsage = true, bool ca = false,
        bool expired = false, bool notYetValid = false, bool wrongKey = false,
        ECCurve? curve = null, bool signingUsage = true)
    {
        using var key = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        var builder = new CertificateRequest("CN=phone", key, HashAlgorithmName.SHA256);
        builder.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        builder.CertificateExtensions.Add(new X509KeyUsageExtension(
            signingUsage ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyAgreement, true));
        builder.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(clientUsage ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")
        }, true));
        // The invalid key-usage fixture must not attach an ECDSA private key on Windows.
        using var certificate = builder.Create(builder.SubjectName, X509SignatureGenerator.CreateForECDsa(key),
            notYetValid ? Now.AddDays(1) : Now.AddDays(-2), expired ? Now.AddDays(-1) : Now.AddDays(2),
            RandomNumberGenerator.GetBytes(16));
        var der = certificate.RawData;
        var id = Guid.NewGuid().ToString("D");
        var token = new PairingChallengeStore(TimeProvider.System).Issue().Token;
        const string name = "My phone";
        var payload = Encoding.UTF8.GetBytes(PairingProof.Transcript(id, name, token,
            Convert.ToHexStringLower(SHA256.HashData(der))));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = (wrongKey ? other : key).SignData(payload, HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        return new PairingRequest(id, name, token, Convert.ToBase64String(der), Convert.ToBase64String(signature));
    }

    [Fact]
    public void ValidProofBindsIdentityAndCertificate()
    {
        var request = Request();
        var identity = PairingProof.Verify(request, Now);
        Assert.NotNull(identity);
        Assert.Equal(Guid.Parse(request.DeviceId), identity.DeviceId);
        Assert.Equal(request.DisplayName, identity.DisplayName);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(request.CertificateDer))),
            identity.CertificateSha256);
    }

    [Fact]
    public void RejectsChangedFieldsAndWrongPrivateKey()
    {
        var request = Request();
        Assert.Null(PairingProof.Verify(request with { DeviceId = Guid.NewGuid().ToString("D") }, Now));
        Assert.Null(PairingProof.Verify(request with { DisplayName = "Another phone" }, Now));
        Assert.Null(PairingProof.Verify(request with { Token = new string('A', 43) }, Now));
        Assert.Null(PairingProof.Verify(request with { CertificateDer = Request().CertificateDer }, Now));
        Assert.Null(PairingProof.Verify(Request(wrongKey: true), Now));
    }

    [Fact]
    public void RejectsInvalidCertificatePolicy()
    {
        Assert.Null(PairingProof.Verify(Request(clientUsage: false), Now));
        Assert.Null(PairingProof.Verify(Request(ca: true), Now));
        Assert.Null(PairingProof.Verify(Request(expired: true), Now));
        Assert.Null(PairingProof.Verify(Request(notYetValid: true), Now));
        Assert.Null(PairingProof.Verify(Request(curve: ECCurve.NamedCurves.nistP384), Now));
        Assert.Null(PairingProof.Verify(Request(signingUsage: false), Now));
    }

    [Fact]
    public void RejectsMalformedAndOversizedInputsWithoutThrowing()
    {
        var request = Request();
        Assert.Null(PairingProof.Verify(null, Now));
        Assert.Null(PairingProof.Verify(request with { DisplayName = "\uD800" }, Now));
        Assert.Null(PairingProof.Verify(request with { DeviceId = Guid.Empty.ToString("D") }, Now));
        Assert.Null(PairingProof.Verify(request with { DeviceId = "bad" }, Now));
        Assert.Null(PairingProof.Verify(request with { DisplayName = "phone\nadmin" }, Now));
        Assert.Null(PairingProof.Verify(request with { DisplayName = " " }, Now));
        Assert.Null(PairingProof.Verify(request with { DisplayName = new string('x', 129) }, Now));
        Assert.Null(PairingProof.Verify(request with { CertificateDer = new string('A', 8193) }, Now));
        Assert.Null(PairingProof.Verify(request with { CertificateDer = "AQID" }, Now));
        Assert.Null(PairingProof.Verify(request with { CertificateDer = "!" }, Now));
        Assert.Null(PairingProof.Verify(request with { ProofSignature = "!" }, Now));
        Assert.Null(PairingProof.Verify(request with { ProofSignature = new string('A', 2049) }, Now));
        Assert.Null(PairingProof.Verify(request with { ProofSignature = request.ProofSignature + "\n" }, Now));
        Assert.Null(PairingProof.Verify(request with { Token = null! }, Now));
        Assert.Null(PairingProof.Verify(request with { CertificateDer = null! }, Now));
        Assert.Null(PairingProof.Verify(request with { DisplayName = null! }, Now));
    }
}
