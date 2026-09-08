using System.ComponentModel;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using PhoneTransfer.Application.Files;
using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Domain;
using PhoneTransfer.Infrastructure.Discovery;
using PhoneTransfer.Infrastructure.Persistence;
using PhoneTransfer.Infrastructure.Security;
using PhoneTransfer.Infrastructure.Storage;
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
    private readonly DurableFileTransferService fileTransfers;
    private WindowsMdnsAdvertiser? mdns;
    public PairingCoordinator Pairing { get; }
    public bool MdnsAvailable => mdns is not null;

    private WindowsServerRuntime(string directory, IPAddress address)
    {
        this.address = address;
        identity = new DeviceIdentityFile(directory);
        devices = new SqliteDeviceRegistry(Path.Combine(directory, "devices.db"));
        certificate = new WindowsServerCertificate().GetOrCreate(identity.DeviceId);
        Pairing = new PairingCoordinator(devices, TimeProvider.System);
        bootstrap = PairingHost.Create(Pairing, certificate, address, BootstrapPort);
        var shareConfigurations = new WindowsShareConfigurationStore(directory);
        var fileSystem = new WindowsShareFileSystem();
        var journal = new SqliteTransferJournal(Path.Combine(directory, "transfers.db"));
        fileTransfers = new DurableFileTransferService(shareConfigurations, fileSystem, fileSystem, journal,
            id => devices.List().FirstOrDefault(device => device.DeviceId == id));
        api = ServerHost.Create(identity, certificate, devices.Authorize, address, ApiPort,
            device => devices.TryTouchLastSeen(device), fileTransfers);
    }

    public static async Task<WindowsServerRuntime> StartAsync(string directory, IPAddress address, CancellationToken token)
    {
        var runtime = new WindowsServerRuntime(directory, address);
        try
        {
            runtime.fileTransfers.Initialize();
            await runtime.api.StartAsync(token).ConfigureAwait(false);
            await runtime.bootstrap.StartAsync(token).ConfigureAwait(false);
            await runtime.StartMdnsAsync(token).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartMdnsAsync(CancellationToken token)
    {
        var interfaceIndex = LanAdapters.FindInterfaceIndex(address);
        var advertisement = interfaceIndex is uint index
            ? MdnsAdvertisement.Create(identity.DeviceId, address, ApiPort, index)
            : null;
        if (advertisement is null) return;

        WindowsMdnsAdvertiser candidate;
        try
        {
            candidate = WindowsMdnsAdvertiser.Start(advertisement);
        }
        catch (Exception exception) when (exception is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            // QR/manual endpoints remain available on networks or Windows builds where DNS-SD cannot advertise.
            return;
        }

        try
        {
            var status = await candidate.Registration.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
            if (status == 0)
            {
                mdns = candidate;
                return;
            }
        }
        catch (TimeoutException)
        {
            // Do not report mDNS as available until Windows confirms registration.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await candidate.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await candidate.DisposeAsync().ConfigureAwait(false);
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

    public Task<bool> RevokeAsync(Guid id) => Task.Run(() =>
    {
        return fileTransfers.RevokeDevice(id, () => devices.Revoke(id));
    });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        fileTransfers.StopAdmission();
        Pairing.ClosePairing();
        var discovery = mdns;
        mdns = null;
        if (discovery is not null) await discovery.DisposeAsync().ConfigureAwait(false);
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
            finally
            {
                fileTransfers.Dispose();
                certificate.Dispose();
            }
        }
    }
}
