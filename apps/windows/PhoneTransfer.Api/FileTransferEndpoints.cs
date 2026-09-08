using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using PhoneTransfer.Protocol;
using ProtocolCreateTransfer = PhoneTransfer.Protocol.CreateTransfer;
using ProtocolFileEntry = PhoneTransfer.Protocol.FileEntry;
using ProtocolTransfer = PhoneTransfer.Protocol.Transfer;

namespace PhoneTransfer.Api;

public static class FileTransferEndpoints
{
    private const int MaximumJsonBytes = 128 * 1024;
    private const int DownloadBufferBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapFileTransferEndpoints(this IEndpointRouteBuilder endpoints, BasicFileTransferService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        endpoints.MapGet("/api/v1/shares", (HttpContext context) => ListShares(context, service));
        endpoints.MapGet("/api/v1/shares/{shareId}/entries", (HttpContext context) => ListEntries(context, service));
        endpoints.MapPost("/api/v1/transfers", (HttpContext context) => CreateTransferAsync(context, service));
        endpoints.MapGet("/api/v1/transfers/{transferId}", (HttpContext context) => GetTransfer(context, service));
        endpoints.MapDelete("/api/v1/transfers/{transferId}", (HttpContext context) => CancelTransfer(context, service));
        endpoints.MapMethods("/api/v1/transfers/{transferId}/content", ["PATCH"],
            (HttpContext context) => AppendChunkAsync(context, service));
        endpoints.MapPost("/api/v1/transfers/{transferId}/complete", (HttpContext context) => CompleteTransfer(context, service));
        endpoints.MapGet("/api/v1/shares/{shareId}/content", (HttpContext context) => DownloadAsync(context, service));
    }

    private static IResult ListShares(HttpContext context, BasicFileTransferService service) => Execute(context, () =>
    {
        var items = service.ListShares(Device(context))
            .Select(item => new Share(item.Id.ToString("D"), item.Name, item.Writable))
            .ToArray();
        return Results.Ok(new ShareList(items));
    });

    private static IResult ListEntries(HttpContext context, BasicFileTransferService service) => Execute(context, () =>
    {
        var shareId = RouteGuid(context, "shareId", "INVALID_SHARE_ID");
        if (!context.Request.Query.ContainsKey("path"))
            throw new BasicFileTransferException("INVALID_PATH", "The path query parameter is required.");
        var cursor = context.Request.Query["cursor"].ToString();
        if (cursor.Length != 0)
            throw new BasicFileTransferException("CURSOR_UNSUPPORTED", "Pagination cursors are not available yet.");
        var path = ParsePath(context.Request.Query["path"].ToString(), allowRoot: true);
        var items = service.ListEntries(Device(context), shareId, path)
            .Select(item => new ProtocolFileEntry(
                item.Name,
                item.RelativePath,
                item.IsDirectory ? "directory" : "file",
                item.Size,
                item.ModifiedAt.ToString("O")))
            .ToArray();
        return Results.Ok(new FileList(items, string.Empty));
    });

    private static async Task<IResult> CreateTransferAsync(HttpContext context, BasicFileTransferService service)
    {
        try
        {
            var bytes = await ReadBoundedBodyAsync(context, MaximumJsonBytes).ConfigureAwait(false);
            ProtocolCreateTransfer? request;
            try
            {
                request = JsonSerializer.Deserialize<ProtocolCreateTransfer>(bytes, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new BasicFileTransferException("INVALID_JSON", "The transfer metadata is not valid JSON.", false, exception);
            }
            if (request is null)
                throw new BasicFileTransferException("INVALID_JSON", "Transfer metadata is required.");

            var shareId = ParseGuid(request.ShareId, "INVALID_SHARE_ID");
            var idempotencyKey = ParseGuid(request.IdempotencyKey, "INVALID_IDEMPOTENCY_KEY");
            var destination = ParsePath(request.RelativePath, allowRoot: false);
            var snapshot = service.CreateTransfer(
                Device(context), shareId, destination, request.TotalSize, request.Sha256 ?? string.Empty, idempotencyKey);
            var dto = ToTransfer(snapshot);
            return Results.Created($"/api/v1/transfers/{snapshot.TransferId:D}", dto);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Error(context, exception);
        }
    }

    private static IResult GetTransfer(HttpContext context, BasicFileTransferService service) => Execute(context, () =>
        Results.Ok(ToTransfer(service.GetTransfer(Device(context), RouteGuid(context, "transferId", "INVALID_TRANSFER_ID")))));

    private static IResult CancelTransfer(HttpContext context, BasicFileTransferService service) => Execute(context, () =>
        Results.Ok(ToTransfer(service.Cancel(Device(context), RouteGuid(context, "transferId", "INVALID_TRANSFER_ID")))));

    private static IResult CompleteTransfer(HttpContext context, BasicFileTransferService service) => Execute(context, () =>
        Results.Ok(ToTransfer(service.Complete(Device(context), RouteGuid(context, "transferId", "INVALID_TRANSFER_ID")))));

    private static async Task<IResult> AppendChunkAsync(HttpContext context, BasicFileTransferService service)
    {
        try
        {
            var transferId = RouteGuid(context, "transferId", "INVALID_TRANSFER_ID");
            if (!long.TryParse(context.Request.Headers["Upload-Offset"].ToString(), out var offset) || offset < 0)
                throw new BasicFileTransferException("INVALID_UPLOAD_OFFSET", "Upload-Offset must be a non-negative integer.");

            // Validate ownership and the currently committed offset before allocating a chunk buffer.
            var current = service.GetTransfer(Device(context), transferId);
            if (offset != current.TransferredBytes)
                throw new BasicFileTransferException("OFFSET_MISMATCH", "The chunk does not match the committed upload offset.", true);
            var bytes = await ReadBoundedBodyAsync(context, TransferOffset.MaximumChunkBytes).ConfigureAwait(false);
            if (bytes.Length == 0)
                throw new BasicFileTransferException("INVALID_CHUNK", "Upload chunks must not be empty.");
            return Results.Ok(ToTransfer(service.Append(Device(context), transferId, offset, bytes)));
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Error(context, exception);
        }
    }

    private static async Task DownloadAsync(HttpContext context, BasicFileTransferService service)
    {
        DownloadLease? lease = null;
        try
        {
            var shareId = RouteGuid(context, "shareId", "INVALID_SHARE_ID");
            if (!context.Request.Query.ContainsKey("path"))
                throw new BasicFileTransferException("INVALID_PATH", "The path query parameter is required.");
            var path = ParsePath(context.Request.Query["path"].ToString(), allowRoot: false);
            lease = service.OpenDownload(Device(context), shareId, path);

            var start = 0L;
            var status = StatusCodes.Status200OK;
            var range = context.Request.Headers["Range"].ToString();
            if (range.Length != 0)
            {
                if (!string.Equals(context.Request.Headers["If-Match"].ToString(), lease.ETag, StringComparison.Ordinal))
                {
                    await Error(context, new BasicFileTransferException(
                        "ETAG_MISMATCH", "If-Match must match the current file ETag.", true), StatusCodes.Status412PreconditionFailed)
                        .ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }
                start = ParseRangeStart(range, lease.Length);
                status = StatusCodes.Status206PartialContent;
                context.Response.Headers["Content-Range"] = $"bytes {start}-{lease.Length - 1}/{lease.Length}";
            }

            context.Response.StatusCode = status;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = lease.Length - start;
            context.Response.Headers["Accept-Ranges"] = "bytes";
            context.Response.Headers["ETag"] = lease.ETag;
            var buffer = new byte[DownloadBufferBytes];
            var offset = start;
            while (offset < lease.Length)
            {
                var read = lease.Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, lease.Length - offset)));
                if (read <= 0) throw new IOException("DOWNLOAD_FILE_ENDED_EARLY");
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted).ConfigureAwait(false);
                offset += read;
            }
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            if (!context.Response.HasStarted)
                await Error(context, exception).ExecuteAsync(context).ConfigureAwait(false);
            else
                context.Abort();
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContext context, int maximumBytes)
    {
        if (context.Request.ContentLength is long contentLength && contentLength > maximumBytes)
            throw new BasicFileTransferException("REQUEST_TOO_LARGE", "The request body exceeds the allowed size.");
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(length, buffer.Length - length), context.RequestAborted)
                    .ConfigureAwait(false);
                if (read == 0) break;
                length += read;
            }
        }
        catch (BadHttpRequestException exception)
        {
            throw new BasicFileTransferException("REQUEST_TOO_LARGE", "The request body exceeds the allowed size.", false, exception);
        }
        if (length > maximumBytes)
            throw new BasicFileTransferException("REQUEST_TOO_LARGE", "The request body exceeds the allowed size.");
        return buffer[..length];
    }

    private static long ParseRangeStart(string value, long length)
    {
        if (!value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(',') || !value.EndsWith("-", StringComparison.Ordinal))
            throw new BasicFileTransferException("INVALID_RANGE", "Only a single open-ended byte range is supported.");
        var number = value[6..^1];
        if (!long.TryParse(number, out var start) || start < 0 || start >= length)
            throw new BasicFileTransferException("INVALID_RANGE", "The requested byte range is not satisfiable.");
        return start;
    }

    private static PairedDevice Device(HttpContext context) =>
        context.Items.TryGetValue(typeof(PairedDevice), out var value) && value is PairedDevice device
            ? device
            : throw new BasicFileTransferException("PERMISSION_DENIED", "The paired device is not available for this request.");

    private static RelativeSharePath ParsePath(string? value, bool allowRoot)
    {
        try
        {
            return RelativeSharePath.Parse(value ?? string.Empty, allowRoot);
        }
        catch (ArgumentException exception)
        {
            throw new BasicFileTransferException("INVALID_PATH", "The relative share path is invalid.", false, exception);
        }
    }

    private static Guid RouteGuid(HttpContext context, string name, string code) =>
        ParseGuid(context.Request.RouteValues[name]?.ToString(), code);

    private static Guid ParseGuid(string? value, string code)
    {
        if (!Guid.TryParseExact(value, "D", out var result) || result == Guid.Empty)
            throw new BasicFileTransferException(code, "The UUID value is invalid.");
        return result;
    }

    private static ProtocolTransfer ToTransfer(TransferSnapshot transfer) => new(
        transfer.TransferId.ToString("D"),
        transfer.FileName,
        transfer.TotalSize,
        transfer.TransferredBytes,
        transfer.Sha256,
        transfer.State switch
        {
            TransferState.Created => "created",
            TransferState.Transferring => "transferring",
            TransferState.Paused => "paused",
            TransferState.Verifying => "verifying",
            TransferState.Completed => "completed",
            TransferState.Failed => "failed",
            TransferState.Cancelled => "cancelled",
            _ => throw new InvalidOperationException("Unknown transfer state.")
        },
        transfer.UpdatedAt.ToString("O"));

    private static IResult Execute(HttpContext context, Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return Error(context, exception);
        }
    }

    private static bool IsExpected(Exception exception) =>
        exception is BasicFileTransferException or IOException or InvalidDataException or ArgumentException;

    private static IResult Error(HttpContext context, Exception exception, int? forcedStatus = null)
    {
        var transfer = exception as BasicFileTransferException;
        var code = transfer?.Code ?? "FILESYSTEM_UNAVAILABLE";
        var retryable = transfer?.Retryable ?? true;
        var message = transfer?.Message ?? "The file operation is unavailable.";
        var status = forcedStatus ?? code switch
        {
            "PERMISSION_DENIED" => StatusCodes.Status403Forbidden,
            "SHARE_NOT_CONFIGURED" or "SHARE_NOT_FOUND" or "TRANSFER_NOT_FOUND" or "PATH_UNAVAILABLE" => StatusCodes.Status404NotFound,
            "REQUEST_TOO_LARGE" => StatusCodes.Status413PayloadTooLarge,
            "HASH_MISMATCH" => StatusCodes.Status422UnprocessableEntity,
            "STAGING_QUOTA_EXCEEDED" => StatusCodes.Status507InsufficientStorage,
            "IDEMPOTENCY_CONFLICT" or "OFFSET_MISMATCH" or "TRANSFER_STATE_CONFLICT" or "TRANSFER_INCOMPLETE" or
                "DESTINATION_EXISTS" or "DESTINATION_CONFLICT" or "TOO_MANY_ACTIVE_TRANSFERS" or "TOO_MANY_TRANSFERS" or
                "WRITE_FAILED" or "VERIFY_FAILED" => StatusCodes.Status409Conflict,
            "INVALID_RANGE" => StatusCodes.Status416RangeNotSatisfiable,
            _ when code.StartsWith("INVALID_", StringComparison.Ordinal) || code == "CURSOR_UNSUPPORTED" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status409Conflict
        };
        return Results.Json(new ApiError(code, message, retryable, context.TraceIdentifier), statusCode: status);
    }
}
