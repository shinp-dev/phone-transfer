using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PhoneTransfer.Api;
using PhoneTransfer.Application;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;

namespace PhoneTransfer.Host;

public static class ServerHost
{
    // No development HTTP listener or accept-any certificate fallback.
    public static WebApplication Create(IServerIdentity identity, X509Certificate2 serverCertificate,
        Func<X509Certificate2, PairedDevice?> authorize, IPAddress address, int port,
        Action<PairedDevice>? onAuthorizedRequest = null, BasicFileTransferService? fileTransfer = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "O";
            options.UseUtcTimestamp = true;
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            // One extra byte lets the file API return a typed 413 for a 4 MiB + 1 chunk.
            options.Limits.MaxRequestBodySize = TransferOffset.MaximumChunkBytes + 1L;
            options.Listen(address, port, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => authorize(certificate) is not null;
            }));
        });
        builder.Services.AddSingleton(identity);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            // TLS sessions are reusable: revocation must also be checked on every request.
            var device = certificate is null ? null : authorize(certificate);
            if (device is null)
            {
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new PhoneTransfer.Protocol.ApiError(
                    "DEVICE_NOT_AUTHORIZED", "Device is not authorized.", false, context.TraceIdentifier), context.RequestAborted);
                return;
            }
            context.Items[typeof(PairedDevice)] = device;
            onAuthorizedRequest?.Invoke(device);
            await next(context);
        });
        app.MapServerEndpoints(fileTransfer);
        return app;
    }
}
