using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PhoneTransfer.Application;
using PhoneTransfer.Application.Text;
using PhoneTransfer.Domain;
using PhoneTransfer.Host;
using PhoneTransfer.Protocol;
using Xunit;

namespace PhoneTransfer.Tests;

public class TextMessageApiIntegrationTests
{
    private sealed class Identity : IServerIdentity
    {
        public Guid DeviceId { get; } = Guid.NewGuid();
        public string DisplayName => "Test PC";
    }

    [Fact]
    public async Task AndroidToPcTextIsIdempotentAndUrlRequiresExplicitSafeKind()
    {
        using var serverCertificate = PairingTestCertificates.Create(server: true);
        using var clientCertificate = PairingTestCertificates.Create();
        var certificateHash = Convert.ToHexStringLower(clientCertificate.GetCertHash(HashAlgorithmName.SHA256));
        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice(Guid.NewGuid(), "Test phone", certificateHash, now, now,
            DevicePermissions.All, false);
        var received = new List<ReceivedTextMessage>();
        var textService = new TextMessageService(message => received.Add(message));

        await using var server = ServerHost.Create(new Identity(), serverCertificate,
            certificate => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)) == certificateHash
                ? device
                : null,
            IPAddress.Loopback, 0, textMessages: textService);
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = Client(address, serverCertificate, clientCertificate);

        var key = Guid.NewGuid().ToString("D");
        var request = new SendText("url", "https://example.com/path?q=1", key);
        using var firstResponse = await client.PostAsJsonAsync("/api/v1/text", request);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var first = await firstResponse.Content.ReadFromJsonAsync<TextEntry>();
        Assert.NotNull(first);
        Assert.Equal(device.DeviceId.ToString("D"), first.SourceDeviceId);
        Assert.Equal(request.Content, first.Content);

        using var retryResponse = await client.PostAsJsonAsync("/api/v1/text", request);
        Assert.Equal(HttpStatusCode.Created, retryResponse.StatusCode);
        var retry = await retryResponse.Content.ReadFromJsonAsync<TextEntry>();
        Assert.NotNull(retry);
        Assert.Equal(first.Id, retry.Id);
        Assert.Single(received);

        using var conflictResponse = await client.PostAsJsonAsync("/api/v1/text",
            new SendText("url", "https://example.com/other", key));
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Single(received);

        using var invalidUrlResponse = await client.PostAsJsonAsync("/api/v1/text",
            new SendText("url", "javascript:alert(1)", Guid.NewGuid().ToString("D")));
        Assert.Equal(HttpStatusCode.BadRequest, invalidUrlResponse.StatusCode);
        Assert.Single(received);

        await server.StopAsync();
    }

    [Fact]
    public void TextSendPermissionIsEnforcedBeforePresentation()
    {
        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice(Guid.NewGuid(), "Read only", "hash", now, now,
            DevicePermissions.Browse, false);
        var presented = 0;
        var service = new TextMessageService(_ => presented++);

        var error = Assert.Throws<TextMessageException>(() => service.Send(device,
            new SendText("plainText", "hello", Guid.NewGuid().ToString("D"))));

        Assert.Equal("PERMISSION_DENIED", error.Code);
        Assert.Equal(0, presented);
    }

    private static HttpClient Client(string address, X509Certificate2 serverCertificate,
        X509Certificate2 clientCertificate)
    {
        var serverHash = serverCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
                certificate.GetCertHashString(HashAlgorithmName.SHA256) == serverHash
        };
        handler.ClientCertificates.Add(clientCertificate);
        return new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
    }
}
