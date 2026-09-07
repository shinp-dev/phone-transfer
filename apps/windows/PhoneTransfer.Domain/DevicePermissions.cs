namespace PhoneTransfer.Domain;

[Flags]
public enum DevicePermissions { None = 0, Browse = 1, Upload = 2, Download = 4, TextSend = 8, TextReceive = 16, All = 31 }

public sealed record PairedDevice(Guid DeviceId, string DisplayName, string CertificateSha256,
    DateTimeOffset RegisteredAt, DateTimeOffset LastSeenAt, DevicePermissions Permissions, bool Revoked)
{
    public bool Allows(DevicePermissions required) => !Revoked && required != DevicePermissions.None
        && (Permissions & required) == required;
}
