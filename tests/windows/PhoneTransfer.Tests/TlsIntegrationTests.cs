using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PhoneTransfer.Application;
using PhoneTransfer.Host;
using Xunit;

namespace PhoneTransfer.Tests;

public class TlsIntegrationTests
{
    private sealed class Identity : IServerIdentity
    {
        public Guid DeviceId { get; } = Guid.NewGuid();
        public string DisplayName => "Test PC";
    }

    private static X509Certificate2 Certificate(string name, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")
        }, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null,
            X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
    }

    [Fact]
    public async Task RealKestrelRejectsUnknownCertificateAndRevokedKeepAliveClient()
    {
        using var serverCertificate = Certificate("server", true);
        using var allowed = Certificate("phone", false);
        using var unknown = Certificate("unknown", false);
        var revoked = 0;
        var expected = allowed.GetCertHashString(HashAlgorithmName.SHA256);
        await using var server = ServerHost.Create(new Identity(), serverCertificate,
            cert => Volatile.Read(ref revoked) == 0 && cert.GetCertHashString(HashAlgorithmName.SHA256) == expected,
            IPAddress.Loopback, 0);
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        HttpClient Client(X509Certificate2? certificate)
        {
            var handler = new HttpClientHandler();
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null &&
                cert.GetCertHashString(HashAlgorithmName.SHA256) == serverCertificate.GetCertHashString(HashAlgorithmName.SHA256);
            if (certificate is not null) handler.ClientCertificates.Add(certificate);
            return new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
        }
        using var authorized = Client(allowed);
        using var first = await authorized.GetAsync("/api/v1/info");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        foreach (var certificate in new X509Certificate2?[] { unknown, null })
        {
            using var denied = Client(certificate);
            await Assert.ThrowsAsync<HttpRequestException>(() => denied.GetAsync("/api/v1/info"));
        }
        Interlocked.Exchange(ref revoked, 1);
        using var afterRevocation = await authorized.GetAsync("/api/v1/info");
        Assert.Equal(HttpStatusCode.Forbidden, afterRevocation.StatusCode);
        await server.StopAsync();
    }
}
