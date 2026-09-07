namespace PhoneTransfer.Application;

public interface IServerIdentity
{
    Guid DeviceId { get; }
    string DisplayName { get; }
}
