using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PhoneTransfer.Host;
using PhoneTransfer.Protocol;
using Xunit;

namespace PhoneTransfer.Tests;

public class WindowsServerRuntimeTests
{
    [Fact]
    public async Task ProductionRuntimePairsWithCngTlsAndRestoresPairingAfterRestart()
    {
        if (!OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(Path.GetTempPath(), "phone-transfer-tests", Guid.NewGuid().ToString("N"));
        Guid? deviceId = null;
        try
        {
            await using var runtime = await WindowsServerRuntime.StartAsync(directory, IPAddress.Loopback, CancellationToken.None);
            var qr = runtime.NewQr();
            deviceId = Guid.Parse(qr.DeviceId);
            using var phone = PairingTestCertificates.Create();
            HttpClient Client(string endpoint, bool paired)
            {
                var handler = new HttpClientHandler();
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    using var key = certificate?.GetECDsaPublicKey();
                    return key is not null && Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) == qr.ServerSpkiSha256;
                };
                if (paired) handler.ClientCertificates.Add(phone);
                return new HttpClient(handler) { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromSeconds(15) };
            }
            using var bootstrap = Client(qr.Endpoint, false);
            using var response = await bootstrap.PostAsJsonAsync("/pairing/v1/requests", PairingTestCertificates.Request(phone, qr.Token));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var request = (await response.Content.ReadFromJsonAsync<PairingStatus>())!;
            Assert.True(runtime.Pairing.Approve(Guid.Parse(request.RequestId)));
            runtime.Pairing.EndChallenge();
            using var api = Client(qr.ApiEndpoint, true);
            Assert.Equal(qr.DeviceId, (await api.GetFromJsonAsync<ServerInfo>("/api/v1/info"))!.DeviceId);
            await runtime.DisposeAsync();
            await using var restarted = await WindowsServerRuntime.StartAsync(directory, IPAddress.Loopback, CancellationToken.None);
            var renewedQr = restarted.NewQr();
            Assert.Equal(qr.DeviceId, renewedQr.DeviceId);
            Assert.Equal(qr.ServerSpkiSha256, renewedQr.ServerSpkiSha256);
            Assert.Single(await restarted.GetDevicesAsync());
            using var restored = Client(renewedQr.ApiEndpoint, true);
            Assert.Equal(qr.DeviceId, (await restored.GetFromJsonAsync<ServerInfo>("/api/v1/info"))!.DeviceId);
        }
        finally
        {
            if (deviceId is Guid id)
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                foreach (var certificate in store.Certificates)
                {
                    using (certificate)
                    {
                        if (certificate.Subject == $"CN=PhoneTransfer-{id:D}") store.Remove(certificate);
                    }
                }
                var keyName = $"PhoneTransfer.Server.{id:D}";
                if (CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
                {
                    using var key = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                    key.Delete();
                }
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
