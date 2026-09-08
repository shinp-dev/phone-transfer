using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PhoneTransfer.Application;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Host;
using PhoneTransfer.Infrastructure.Persistence;
using PhoneTransfer.Protocol;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed class PairingApiIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "phone-transfer-tests", Guid.NewGuid().ToString("N"));

    private sealed class Identity : IServerIdentity
    {
        public Guid DeviceId { get; } = Guid.NewGuid();
        public string DisplayName => "Integration PC";
    }

    private static HttpClient Client(WebApplication app, X509Certificate2 server, X509Certificate2? client = null)
    {
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) => certificate is not null &&
            certificate.GetCertHashString(HashAlgorithmName.SHA256) == server.GetCertHashString(HashAlgorithmName.SHA256);
        if (client is not null) handler.ClientCertificates.Add(client);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(15) };
    }

    [Fact]
    public async Task RealHttpsPairingRequiresLocalApprovalAndRevocationRejectsKeepAlive()
    {
        using var serverCertificate = PairingTestCertificates.Create(server: true);
        using var phoneCertificate = PairingTestCertificates.Create();
        var registry = new SqliteDeviceRegistry(Path.Combine(directory, "devices.db"));
        var coordinator = new PairingCoordinator(registry, TimeProvider.System);
        await using var bootstrap = PairingHost.Create(coordinator, serverCertificate, IPAddress.Loopback, 0);
        await using var transfer = ServerHost.Create(new Identity(), serverCertificate,
            registry.Authorize, IPAddress.Loopback, 0, device => registry.TryTouchLastSeen(device));
        await bootstrap.StartAsync();
        await transfer.StartAsync();
        using (var notPaired = Client(transfer, serverCertificate, phoneCertificate))
            await Assert.ThrowsAsync<HttpRequestException>(() => notPaired.GetAsync("/api/v1/info"));
        using var client = Client(bootstrap, serverCertificate);
        var request = PairingTestCertificates.Request(phoneCertificate, coordinator.IssueChallenge().Token);
        using var submitted = await client.PostAsJsonAsync("/pairing/v1/requests", request);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var status = (await submitted.Content.ReadFromJsonAsync<PairingStatus>())!;
        var path = $"/pairing/v1/requests/{status.RequestId}";
        using var unsigned = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, unsigned.StatusCode);
        client.DefaultRequestHeaders.Add("X-Pairing-Proof",
            PairingTestCertificates.StatusProof(phoneCertificate, Guid.Parse(status.RequestId)));
        Assert.Equal("pending", (await client.GetFromJsonAsync<PairingStatus>(path))!.Status);
        Assert.True(coordinator.Approve(Guid.Parse(status.RequestId)));
        Assert.Equal("approved", (await client.GetFromJsonAsync<PairingStatus>(path))!.Status);
        using var authenticated = Client(transfer, serverCertificate, phoneCertificate);
        using var first = await authenticated.GetAsync("/api/v1/info");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.True(registry.Revoke(Guid.Parse(request.DeviceId)));
        using var revoked = await authenticated.GetAsync("/api/v1/info");
        Assert.Equal(HttpStatusCode.Forbidden, revoked.StatusCode);
        Assert.Equal("DEVICE_NOT_AUTHORIZED", (await revoked.Content.ReadFromJsonAsync<ApiError>())!.Code);
        using var reconnect = Client(transfer, serverCertificate, phoneCertificate);
        await Assert.ThrowsAsync<HttpRequestException>(() => reconnect.GetAsync("/api/v1/info"));
        using var wrongListener = await client.GetAsync("/api/v1/info");
        Assert.Equal(HttpStatusCode.NotFound, wrongListener.StatusCode);
        await transfer.StopAsync();
        await bootstrap.StopAsync();
    }

    [Fact]
    public async Task RejectsMalformedOversizedAndExcessRequestsWithTypedErrors()
    {
        using var serverCertificate = PairingTestCertificates.Create(server: true);
        var coordinator = new PairingCoordinator(new SqliteDeviceRegistry(Path.Combine(directory, "devices.db")), TimeProvider.System);
        await using var bootstrap = PairingHost.Create(coordinator, serverCertificate, IPAddress.Loopback, 0);
        await bootstrap.StartAsync();
        using var client = Client(bootstrap, serverCertificate);
        using var malformed = await client.PostAsync("/pairing/v1/requests", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("INVALID_PAIRING_METADATA", (await malformed.Content.ReadFromJsonAsync<ApiError>())!.Code);
        using var unknown = await client.PostAsync("/pairing/v1/requests", new StringContent("{\"unexpected\":true}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        using var oversized = await client.PostAsync("/pairing/v1/requests", new StringContent(
            "{\"displayName\":\"" + new string('a', 130 * 1024) + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        for (var i = 0; i < 5; i++)
        {
            using var invalid = await client.PostAsync("/pairing/v1/requests", new StringContent("null", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, invalid.StatusCode);
        }
        using var limited = await client.PostAsync("/pairing/v1/requests", new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("PAIRING_RATE_LIMITED", (await limited.Content.ReadFromJsonAsync<ApiError>())!.Code);
        await bootstrap.StopAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
