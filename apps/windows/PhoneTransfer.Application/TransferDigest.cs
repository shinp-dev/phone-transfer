using System.Security.Cryptography;

namespace PhoneTransfer.Application;

public static class TransferDigest
{
    public static async Task<bool> VerifyAsync(Stream source, string expected, CancellationToken cancellationToken)
    {
        if (expected.Length != 64 || expected.Any(c => !char.IsAsciiHexDigit(c))) return false;
        var actual = await SHA256.HashDataAsync(source, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expected));
    }
}
