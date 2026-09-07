using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PhoneTransfer.Application.Security;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Application.Pairing;

public sealed class PairingRejectedException(string code) : Exception("Pairing request was rejected.")
{
    public string Code { get; } = code;
}

public sealed record PairingApproval(Guid RequestId, string DisplayName, string ComparisonCode);

public sealed class PairingCoordinator(IPairedDeviceRegistry devices, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly PairingChallengeStore challenges = new(clock);
    private PendingRequest? pending;
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromSeconds(120);

    private sealed class PendingRequest(Guid id, VerifiedPairingIdentity identity, string certificateDer,
        string comparisonCode, long createdAt)
    {
        public Guid Id { get; } = id;
        public VerifiedPairingIdentity Identity { get; } = identity;
        public string CertificateDer { get; } = certificateDer;
        public string ComparisonCode { get; } = comparisonCode;
        public long CreatedAt { get; } = createdAt;
        public string Status { get; set; } = "pending";
    }

    public PairingChallenge IssueChallenge()
    {
        lock (gate)
        {
            pending = null;
            return challenges.Issue();
        }
    }

    public void EndChallenge()
    {
        lock (gate)
        {
            challenges.Invalidate();
            // Preserve the signed status receipt while the phone observes local approval/denial.
            if (pending is not null && CurrentStatus(pending) == "pending") pending.Status = "denied";
        }
    }

    public void ClosePairing()
    {
        lock (gate)
        {
            challenges.Invalidate();
            pending = null;
        }
    }

    public PairingStatus Submit(PairingRequest? request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = PairingProof.Verify(request, clock.GetUtcNow())
            ?? throw new PairingRejectedException("INVALID_PAIRING_PROOF");
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!challenges.TryConsume(request!.Token, () =>
            {
                pending = new PendingRequest(Guid.NewGuid(), identity, request.CertificateDer,
                    ComparisonCode(request), clock.GetTimestamp());
            })) throw new PairingRejectedException("PAIRING_TOKEN_INVALID");
            return Status(pending!);
        }
    }

    public PairingApproval? PendingApproval()
    {
        lock (gate)
        {
            if (pending is null || CurrentStatus(pending) != "pending") return null;
            return new PairingApproval(pending.Id, pending.Identity.DisplayName, pending.ComparisonCode);
        }
    }

    // Only the local application receives this operation. No HTTP approval route exists.
    public bool Approve(Guid requestId)
    {
        lock (gate)
        {
            if (pending is null || pending.Id != requestId || CurrentStatus(pending) != "pending") return false;
            // Persist before reporting approval. A storage failure leaves no successful receipt.
            if (!devices.Register(pending.Identity, pending.CertificateDer))
            {
                pending.Status = "denied";
                return false;
            }
            pending.Status = "approved";
            return true;
        }
    }

    public bool Deny(Guid requestId)
    {
        lock (gate)
        {
            if (pending is null || pending.Id != requestId || CurrentStatus(pending) != "pending") return false;
            pending.Status = "denied";
            return true;
        }
    }

    public PairingStatus GetStatus(Guid requestId, string? signature)
    {
        lock (gate)
        {
            if (pending is null || pending.Id != requestId || !VerifyStatusProof(pending, signature))
                throw new PairingRejectedException("PAIRING_STATUS_NOT_AUTHORIZED");
            return Status(pending);
        }
    }

    public static string StatusTranscript(Guid requestId) => $"phone-transfer/pairing-status/v1\n{requestId:D}";

    public static string ComparisonCode(PairingRequest request)
    {
        var digest = Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(request.CertificateDer)));
        var transcript = Encoding.UTF8.GetBytes(PairingProof.Transcript(request.DeviceId, request.DisplayName,
            request.Token, digest));
        return (BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(transcript)) % 1_000_000)
            .ToString("D6", CultureInfo.InvariantCulture);
    }

    private static bool VerifyStatusProof(PendingRequest request, string? signature)
    {
        if (signature is null || signature.Length is < 1 or > 2048) return false;
        try
        {
            var bytes = Convert.FromBase64String(signature);
            if (Convert.ToBase64String(bytes) != signature) return false;
            using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(request.CertificateDer));
            using var key = certificate.GetECDsaPublicKey();
            return key is not null && key.VerifyData(Encoding.UTF8.GetBytes(StatusTranscript(request.Id)), bytes,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    private string CurrentStatus(PendingRequest request)
    {
        if (clock.GetElapsedTime(request.CreatedAt) >= PendingLifetime) request.Status = "expired";
        return request.Status;
    }

    private PairingStatus Status(PendingRequest request) => new(request.Id.ToString("D"), CurrentStatus(request));
}
