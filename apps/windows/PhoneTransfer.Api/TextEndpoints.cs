using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PhoneTransfer.Application.Text;
using PhoneTransfer.Domain;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Api;

public static class TextEndpoints
{
    private const int MaximumJsonBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapTextEndpoints(this IEndpointRouteBuilder endpoints, TextMessageService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        endpoints.MapPost("/api/v1/text", (Delegate)((HttpContext context) => SendAsync(context, service)));
    }

    private static async Task<IResult> SendAsync(HttpContext context, TextMessageService service)
    {
        try
        {
            var bytes = await ReadBoundedBodyAsync(context).ConfigureAwait(false);
            SendText? request;
            try
            {
                request = JsonSerializer.Deserialize<SendText>(bytes, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new TextMessageException("INVALID_JSON", "The text payload is not valid JSON.", false, exception);
            }
            var entry = service.Send(Device(context), request);
            return Results.Json(entry, statusCode: StatusCodes.Status201Created);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Error(context, exception);
        }
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is long contentLength && contentLength > MaximumJsonBytes)
            throw new TextMessageException("REQUEST_TOO_LARGE", "The request body exceeds the allowed size.");
        var buffer = new byte[MaximumJsonBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length, buffer.Length - length),
                context.RequestAborted).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > MaximumJsonBytes)
            throw new TextMessageException("REQUEST_TOO_LARGE", "The request body exceeds the allowed size.");
        return buffer[..length];
    }

    private static PairedDevice Device(HttpContext context) =>
        context.Items.TryGetValue(typeof(PairedDevice), out var value) && value is PairedDevice device
            ? device
            : throw new TextMessageException("PERMISSION_DENIED", "The paired device is not available for this request.");

    private static bool IsExpected(Exception exception) =>
        exception is TextMessageException or BadHttpRequestException or IOException or OperationCanceledException;

    private static IResult Error(HttpContext context, Exception exception)
    {
        var typed = exception as TextMessageException;
        var code = typed?.Code ?? (exception is OperationCanceledException ? "REQUEST_CANCELLED" : "TEXT_SERVICE_UNAVAILABLE");
        var retryable = typed?.Retryable ?? exception is IOException;
        var message = typed?.Message ?? "The text operation is unavailable.";
        var status = code switch
        {
            "PERMISSION_DENIED" => StatusCodes.Status403Forbidden,
            "IDEMPOTENCY_CONFLICT" => StatusCodes.Status409Conflict,
            "REQUEST_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
            "TEXT_SERVICE_UNAVAILABLE" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new ApiError(code, message, retryable, context.TraceIdentifier), statusCode: status);
    }
}
