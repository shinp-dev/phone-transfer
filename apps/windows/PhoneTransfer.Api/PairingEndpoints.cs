using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Api;

public static class PairingEndpoints
{
    private static readonly JsonSerializerOptions RequestJson = new()
    {
        MaxDepth = 8,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static void MapPairingEndpoints(this IEndpointRouteBuilder endpoints, PairingCoordinator coordinator)
    {
        endpoints.MapPost("/pairing/v1/requests", async (HttpContext context) =>
        {
            if (!context.Request.HasJsonContentType()) return Error(context, 415, "JSON_REQUIRED");
            try
            {
                var request = await context.Request.ReadFromJsonAsync<PairingRequest>(RequestJson, context.RequestAborted);
                var status = coordinator.Submit(request, context.RequestAborted);
                return Results.Json(status, statusCode: StatusCodes.Status202Accepted);
            }
            catch (JsonException) { return Error(context, 400, "INVALID_PAIRING_METADATA"); }
            catch (BadHttpRequestException exception) { return Error(context, exception.StatusCode, "INVALID_PAIRING_METADATA"); }
            catch (PairingRejectedException exception) { return Error(context, 403, exception.Code); }
        }).RequireRateLimiting("pairing-submit");

        endpoints.MapGet("/pairing/v1/requests/{requestId}", (HttpContext context, string requestId) =>
        {
            if (!Guid.TryParseExact(requestId, "D", out var id)) return Error(context, 400, "INVALID_REQUEST_ID");
            try
            {
                return Results.Json(coordinator.GetStatus(id, context.Request.Headers["X-Pairing-Proof"].ToString()));
            }
            catch (PairingRejectedException exception) { return Error(context, 403, exception.Code); }
        }).RequireRateLimiting("pairing-status");
        endpoints.MapFallback((HttpContext context) => Error(context, 404, "ENDPOINT_NOT_FOUND"));
    }

    private static IResult Error(HttpContext context, int status, string code) => Results.Json(
        new ApiError(code, "The pairing request could not be accepted.", false, context.TraceIdentifier), statusCode: status);
}
