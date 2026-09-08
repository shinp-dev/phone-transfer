using PhoneTransfer.Domain;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Application.Text;

public sealed class TextMessageException(string code, string message, bool retryable = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
}

public sealed record ReceivedTextMessage(TextEntry Entry, string SourceDisplayName);

/// <summary>
/// Process-local Android -> PC text inbox. User-visible history is intentionally disabled in this increment;
/// a bounded idempotency window prevents response-loss retries from notifying twice while the PC runtime is alive.
/// </summary>
public sealed class TextMessageService(Action<ReceivedTextMessage>? onReceived = null, TimeProvider? clock = null)
{
    private const int MaximumIdempotencyEntries = 512;
    private const int MaximumContentCharacters = 65_536;
    private readonly object gate = new();
    private readonly Dictionary<(Guid DeviceId, Guid Key), StoredMessage> entries = [];
    private readonly Queue<(Guid DeviceId, Guid Key)> order = new();
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    private sealed record StoredMessage(string Kind, string Content, TextEntry Entry);

    public TextEntry Send(PairedDevice device, SendText? request)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!device.Allows(DevicePermissions.TextSend))
            throw new TextMessageException("PERMISSION_DENIED", "This device cannot send text.");
        if (request is null)
            throw new TextMessageException("INVALID_JSON", "Text content is required.");

        var key = ParseCanonicalUuid(request.IdempotencyKey, "INVALID_IDEMPOTENCY_KEY");
        ValidateKindAndContent(request.Kind, request.Content);

        ReceivedTextMessage notification;
        TextEntry result;
        lock (gate)
        {
            var identity = (device.DeviceId, key);
            if (entries.TryGetValue(identity, out var existing))
            {
                if (!string.Equals(existing.Kind, request.Kind, StringComparison.Ordinal) ||
                    !string.Equals(existing.Content, request.Content, StringComparison.Ordinal))
                    throw new TextMessageException("IDEMPOTENCY_CONFLICT",
                        "The idempotency key was already used for different text.");
                return existing.Entry;
            }

            result = new TextEntry(Guid.NewGuid().ToString("D"), request.Kind, request.Content,
                clock.GetUtcNow().ToString("O"), device.DeviceId.ToString("D"));
            entries.Add(identity, new StoredMessage(request.Kind, request.Content, result));
            order.Enqueue(identity);
            while (order.Count > MaximumIdempotencyEntries)
            {
                var expired = order.Dequeue();
                entries.Remove(expired);
            }
            notification = new ReceivedTextMessage(result, device.DisplayName);
        }

        // UI presentation is not the authority for acceptance. A local display failure must not turn
        // an accepted idempotent message into a transport failure that the phone retries forever.
        if (onReceived is not null)
        {
            try { onReceived(notification); }
            catch (Exception) { /* local presentation is best-effort */ }
        }
        return result;
    }

    private static void ValidateKindAndContent(string kind, string content)
    {
        if (kind is not ("plainText" or "url"))
            throw new TextMessageException("INVALID_TEXT_KIND", "Text kind must be plainText or url.");
        if (string.IsNullOrEmpty(content) || content.Length > MaximumContentCharacters)
            throw new TextMessageException("INVALID_TEXT_CONTENT", "Text content must contain 1 to 65536 characters.");

        if (kind == "url")
        {
            if (!Uri.TryCreate(content, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo))
                throw new TextMessageException("INVALID_URL",
                    "Only absolute http/https URLs without credentials are accepted.");
        }
    }

    private static Guid ParseCanonicalUuid(string value, string code)
    {
        if (!Guid.TryParseExact(value, "D", out var result) || result == Guid.Empty || result.ToString("D") != value)
            throw new TextMessageException(code, "The UUID value is invalid.");
        return result;
    }
}
