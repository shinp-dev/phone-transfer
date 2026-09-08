using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using PhoneTransfer.Application;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Domain;
using PhoneTransfer.Host;
using PhoneTransfer.Infrastructure.Storage;
using PhoneTransfer.Protocol;
using Xunit;

namespace PhoneTransfer.Tests;

[SupportedOSPlatform("windows")]
public sealed class FileTransferApiIntegrationTests : IDisposable
{
    private readonly string fixture = Path.Combine(Path.GetTempPath(), "PhoneTransferApi", Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(fixture, "share");

    public FileTransferApiIntegrationTests()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "existing.txt"), "hello");
    }

    [Fact]
    public async Task AuthenticatedListUploadDownloadAndNoReplaceUseHandleSafeAdapter()
    {
        using var serverCertificate = PairingTestCertificates.Create(server: true);
        using var clientCertificate = PairingTestCertificates.Create();
        var now = DateTimeOffset.UtcNow;
        var device = new PairedDevice(
            Guid.NewGuid(),
            "phone",
            Convert.ToHexStringLower(clientCertificate.GetCertHash(HashAlgorithmName.SHA256)),
            now,
            now,
            DevicePermissions.All,
            false);
        using var transfers = new BasicFileTransferService(new FixedConfigurationStore(Root), new WindowsShareFileSystem());
        await using var server = ServerHost.Create(
            new TestIdentity(),
            serverCertificate,
            certificate => certificate.GetCertHashString(HashAlgorithmName.SHA256) == clientCertificate.GetCertHashString(HashAlgorithmName.SHA256)
                ? device
                : null,
            IPAddress.Loopback,
            0,
            fileTransfer: transfers);
        await server.StartAsync();
        var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = Client(address, serverCertificate, clientCertificate);

        var sharesResponse = await client.GetAsync("/api/v1/shares");
        Assert.Equal(HttpStatusCode.OK, sharesResponse.StatusCode);
        var shares = await sharesResponse.Content.ReadFromJsonAsync<ShareList>();
        var share = Assert.Single(shares!.Items);
        Assert.True(share.Writable);

        var entriesResponse = await client.GetAsync($"/api/v1/shares/{share.Id}/entries?path=");
        Assert.Equal(HttpStatusCode.OK, entriesResponse.StatusCode);
        var entries = await entriesResponse.Content.ReadFromJsonAsync<FileList>();
        var existing = Assert.Single(entries!.Items);
        Assert.Equal("existing.txt", existing.Name);
        Assert.Equal("file", existing.Kind);
        Assert.Equal(5, existing.Size);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, DateTimeOffset.Parse(existing.ModifiedAt));

        var payload = "phone-to-pc"u8.ToArray();
        const string destination = "incoming.bin";
        var create = new CreateTransfer(
            share.Id,
            destination,
            payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)),
            Guid.NewGuid().ToString("D"));
        using var createResponse = await client.PostAsJsonAsync("/api/v1/transfers", create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<Transfer>();
        Assert.Equal("created", created!.Status);

        using var chunk = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/transfers/{created.TransferId}/content");
        chunk.Headers.TryAddWithoutValidation("Upload-Offset", "0");
        chunk.Content = new ByteArrayContent(payload);
        chunk.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var chunkResponse = await client.SendAsync(chunk);
        Assert.Equal(HttpStatusCode.OK, chunkResponse.StatusCode);
        var appended = await chunkResponse.Content.ReadFromJsonAsync<Transfer>();
        Assert.Equal(payload.Length, appended!.TransferredBytes);

        using var completeResponse = await client.PostAsync($"/api/v1/transfers/{created.TransferId}/complete", null);
        Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
        var completed = await completeResponse.Content.ReadFromJsonAsync<Transfer>();
        Assert.Equal("completed", completed!.Status);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(Root, destination)));

        using var download = await client.GetAsync($"/api/v1/shares/{share.Id}/content?path={destination}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(payload, await download.Content.ReadAsByteArrayAsync());
        var etag = Assert.Single(download.Headers.GetValues("ETag"));

        using var rangedRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/shares/{share.Id}/content?path={destination}");
        rangedRequest.Headers.TryAddWithoutValidation("Range", "bytes=2-");
        rangedRequest.Headers.TryAddWithoutValidation("If-Match", etag);
        using var ranged = await client.SendAsync(rangedRequest);
        Assert.Equal(HttpStatusCode.PartialContent, ranged.StatusCode);
        Assert.Equal(payload[2..], await ranged.Content.ReadAsByteArrayAsync());

        var conflictCreate = create with { IdempotencyKey = Guid.NewGuid().ToString("D") };
        using var secondCreateResponse = await client.PostAsJsonAsync("/api/v1/transfers", conflictCreate);
        Assert.Equal(HttpStatusCode.Created, secondCreateResponse.StatusCode);
        var second = await secondCreateResponse.Content.ReadFromJsonAsync<Transfer>();
        using var secondChunk = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/transfers/{second!.TransferId}/content");
        secondChunk.Headers.TryAddWithoutValidation("Upload-Offset", "0");
        secondChunk.Content = new ByteArrayContent(payload);
        using var secondChunkResponse = await client.SendAsync(secondChunk);
        Assert.Equal(HttpStatusCode.OK, secondChunkResponse.StatusCode);
        using var conflict = await client.PostAsync($"/api/v1/transfers/{second.TransferId}/complete", null);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var error = await conflict.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("DESTINATION_EXISTS", error!.Code);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(Root, destination)));

        await server.StopAsync();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        }
        catch (IOException)
        {
            // A failed assertion may leave an OS-backed test certificate/file handle briefly alive.
        }
    }

    private static HttpClient Client(string address, System.Security.Cryptography.X509Certificates.X509Certificate2 serverCertificate,
        System.Security.Cryptography.X509Certificates.X509Certificate2 clientCertificate)
    {
        var expected = serverCertificate.GetCertHashString(HashAlgorithmName.SHA256);
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null && certificate.GetCertHashString(HashAlgorithmName.SHA256) == expected
        };
        handler.ClientCertificates.Add(clientCertificate);
        return new HttpClient(handler) { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
    }

    private sealed class FixedConfigurationStore(string root) : IShareConfigurationStore
    {
        public ShareConfiguration? Read() => new(root);
        public ShareConfiguration Save(string rootPath) => throw new NotSupportedException();
        public void Clear() => throw new NotSupportedException();
    }

    private sealed class TestIdentity : IServerIdentity
    {
        public Guid DeviceId { get; } = Guid.NewGuid();
        public string DisplayName => "Test PC";
    }
}
