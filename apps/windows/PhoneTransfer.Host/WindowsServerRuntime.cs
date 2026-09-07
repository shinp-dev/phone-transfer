using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Domain;
using PhoneTransfer.Infrastructure.Persistence;
using PhoneTransfer.Infrastructure.Security;
using PhoneTransfer.Protocol;

namespace PhoneTransfer.Host;

[SupportedOSPlatform("windows")]
public sealed class WindowsServerRuntime : IAsyncDisposable
{
    private const int ApiPort = 58443;
    private const int BootstrapPort = 58442;
    private int disposed;
    private readonly DeviceIdentityFile identity;
    private readonly SqliteDeviceRegistry devices;
    private readonly X509Certificate2 certificate;
    private readonly IPAddress address;
    private readonly WebApplication bootstrap;
    private readonly WebApplication api;
    public PairingCoordinator Pairing { get; }

    private WindowsServerRuntime(string directory, IPAddress address)
    {
        this.address = address;
        identity = new DeviceIdentityFile(directory);
        devices = new SqliteDeviceRegistry(Path.Combine(directory, "devices.db"));
        certificate = new WindowsServerCertificate().GetOrCreate(identity.DeviceId);
        Pairing = new PairingCoordinator(devices, TimeProvider.System);
        bootstrap = PairingHost.Create(Pairing, certificate, address, BootstrapPort);
        api = ServerHost.Create(identity, certificate, client => devices.Authorize(client) is not null, address, ApiPort);
    }

    public static async Task<WindowsServerRuntime> StartAsync(string directory, IPAddress address, CancellationToken token)
    {
        var runtime = new WindowsServerRuntime(directory, address);
        try
        {
            await runtime.api.StartAsync(token).ConfigureAwait(false);
            await runtime.bootstrap.StartAsync(token).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public PairingQr NewQr()
    {
        var challenge = Pairing.IssueChallenge();
        using var key = certificate.GetECDsaPublicKey()!;
        var pin = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
        return new PairingQr(1, identity.DeviceId.ToString("D"), identity.DisplayName,
            $"https://{address}:{BootstrapPort}", pin, challenge.Token, challenge.ExpiresAt.ToString("O"),
            $"https://{address}:{ApiPort}");
    }

    public Task<IReadOnlyList<PairedDevice>> GetDevicesAsync() => Task.Run(devices.List);
    public Task<bool> RevokeAsync(Guid id) => Task.Run(() => devices.Revoke(id));

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Pairing.ClosePairing();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await Task.WhenAll(bootstrap.StopAsync(deadline.Token), api.StopAsync(deadline.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // Disposal below closes listeners even when graceful shutdown reaches its deadline.
        }
        finally
        {
            try
            {
                try { await bootstrap.DisposeAsync().ConfigureAwait(false); }
                finally { await api.DisposeAsync().ConfigureAwait(false); }
            }
            finally { certificate.Dispose(); }
        }
    }
}
