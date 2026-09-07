using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using PhoneTransfer.Api;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Host;

public static class PairingHost
{
    public static WebApplication Create(PairingCoordinator coordinator, X509Certificate2 certificate,
        IPAddress address, int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 128 * 1024;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(address, port, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = certificate;
                https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }));
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("bootstrap", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = 4,
                    QueueLimit = 0
                }));
            options.AddFixedWindowLimiter("pairing-submit", limiter =>
            {
                limiter.PermitLimit = 8;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
            options.AddFixedWindowLimiter("pairing-status", limiter =>
            {
                limiter.PermitLimit = 120;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                await context.HttpContext.Response.WriteAsJsonAsync(new ApiError(
                    "PAIRING_RATE_LIMITED", "Wait before retrying pairing.", true,
                    context.HttpContext.TraceIdentifier), token);
            };
        });
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapPairingEndpoints(coordinator);
        return app;
    }
}
