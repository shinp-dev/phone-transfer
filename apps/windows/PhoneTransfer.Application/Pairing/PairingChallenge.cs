using System.Security.Cryptography;
using System.Text;

namespace PhoneTransfer.Application.Pairing;

// Deliberately not a record: diagnostic ToString must never include the QR secret.
public sealed class PairingChallenge
{
    public string Token { get; }
    public DateTimeOffset ExpiresAt { get; }

    internal PairingChallenge(string token, DateTimeOffset expiresAt)
    {
        Token = token;
        ExpiresAt = expiresAt;
    }

    public override string ToString() => "PairingChallenge [redacted]";
}

public sealed class PairingChallengeStore(TimeProvider clock)
{
    private readonly object gate = new();
    private byte[]? activeDigest;
    private long issuedAt;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(120);

    public PairingChallenge Issue()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        lock (gate)
        {
            Clear();
            activeDigest = Digest(token);
            issuedAt = clock.GetTimestamp();
            return new PairingChallenge(token, clock.GetUtcNow().Add(Lifetime));
        }
    }

    // The callback creates the pending request under the same lock as consumption.
    // It must be short, synchronous, and must not call back into this store.
    public bool TryConsume(string? token, Action createPendingRequest)
    {
        ArgumentNullException.ThrowIfNull(createPendingRequest);
        if (token is null || token.Length != 43 || token.Any(c =>
            !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        var candidate = Digest(token);
        try
        {
            lock (gate)
            {
                if (activeDigest is null) return false;
                if (clock.GetElapsedTime(issuedAt) >= Lifetime)
                {
                    Clear();
                    return false;
                }
                if (!CryptographicOperations.FixedTimeEquals(activeDigest, candidate)) return false;
                // Even a failed pending-request creation cannot make a QR replayable.
                Clear();
                createPendingRequest();
                return true;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(candidate);
        }
    }

    public void Invalidate()
    {
        lock (gate) Clear();
    }

    private static byte[] Digest(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    private void Clear()
    {
        if (activeDigest is not null) CryptographicOperations.ZeroMemory(activeDigest);
        activeDigest = null;
    }
}
