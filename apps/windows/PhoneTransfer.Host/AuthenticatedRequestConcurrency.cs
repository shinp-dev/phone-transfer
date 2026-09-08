namespace PhoneTransfer.Host;

internal sealed class AuthenticatedRequestConcurrency(int maximumPerDevice)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, int> active = [];

    public bool TryEnter(Guid deviceId)
    {
        if (deviceId == Guid.Empty) return false;
        lock (gate)
        {
            active.TryGetValue(deviceId, out var count);
            if (count >= maximumPerDevice) return false;
            active[deviceId] = count + 1;
            return true;
        }
    }

    public void Exit(Guid deviceId)
    {
        lock (gate)
        {
            if (!active.TryGetValue(deviceId, out var count)) return;
            if (count <= 1) active.Remove(deviceId);
            else active[deviceId] = count - 1;
        }
    }
}
