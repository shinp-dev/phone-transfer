using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PhoneTransfer.Application;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Api;

public static class ServerEndpoints
{
    public static void MapServerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/info", (IServerIdentity identity) => Results.Ok(new ServerInfo(
            identity.DeviceId.ToString("D"), identity.DisplayName, "0.1.0", 1, 1)));
    }
}
