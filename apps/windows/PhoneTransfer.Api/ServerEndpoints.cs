using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PhoneTransfer.Application;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Application.Text;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Api;

public static class ServerEndpoints
{
    public static void MapServerEndpoints(this IEndpointRouteBuilder endpoints, IFileTransferService? fileTransfer = null,
        TextMessageService? textMessages = null)
    {
        endpoints.MapGet("/api/v1/info", (IServerIdentity identity) => Results.Ok(new ServerInfo(
            identity.DeviceId.ToString("D"), identity.DisplayName, "0.1.0", 1, 1)));
        if (fileTransfer is not null) endpoints.MapFileTransferEndpoints(fileTransfer);
        if (textMessages is not null) endpoints.MapTextEndpoints(textMessages);
    }
}
