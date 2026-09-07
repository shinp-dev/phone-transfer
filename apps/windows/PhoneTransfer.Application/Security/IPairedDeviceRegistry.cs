using System.Security.Cryptography.X509Certificates;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Security;

public interface IPairedDeviceRegistry
{
    bool Register(VerifiedPairingIdentity identity, string certificateDer);
    PairedDevice? Authorize(X509Certificate2 certificate);
    IReadOnlyList<PairedDevice> List();
    bool Revoke(Guid deviceId);
}
