using PhoneTransfer.Application.Pairing;
using PhoneTransfer.Infrastructure.Persistence;
using Xunit;

namespace PhoneTransfer.Tests;

public sealed class PairingCoordinatorTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "phone-transfer-tests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(directory, "devices.db");

    [Fact]
    public void RequiresApprovalAndProofForStatusThenPersistsRevocation()
    {
        using var certificate = PairingTestCertificates.Create();
        using var impostor = PairingTestCertificates.Create();
        var registry = new SqliteDeviceRegistry(Database);
        var pairing = new PairingCoordinator(registry, TimeProvider.System);
        var request = PairingTestCertificates.Request(certificate, pairing.IssueChallenge().Token);
        var submitted = pairing.Submit(request);
        var id = Guid.Parse(submitted.RequestId);
        Assert.Null(registry.Authorize(certificate));
        Assert.Throws<PairingRejectedException>(() => pairing.GetStatus(id, null));
        Assert.Throws<PairingRejectedException>(() => pairing.GetStatus(id, PairingTestCertificates.StatusProof(impostor, id)));
        var proof = PairingTestCertificates.StatusProof(certificate, id);
        Assert.Equal("pending", pairing.GetStatus(id, proof).Status);
        Assert.Equal(PairingCoordinator.ComparisonCode(request), pairing.PendingApproval()!.ComparisonCode);
        Assert.Throws<PairingRejectedException>(() => pairing.Submit(request));
        Assert.True(pairing.Approve(id));
        Assert.False(pairing.Approve(id));
        Assert.Equal("approved", pairing.GetStatus(id, proof).Status);
        var reopened = new SqliteDeviceRegistry(Database);
        Assert.Equal(Guid.Parse(request.DeviceId), reopened.Authorize(certificate)!.DeviceId);
        Assert.Null(reopened.Authorize(impostor));
        Assert.True(reopened.Revoke(Guid.Parse(request.DeviceId)));
        Assert.Null(registry.Authorize(certificate));
        Assert.Null(new SqliteDeviceRegistry(Database).Authorize(certificate));
    }

    [Fact]
    public void ExpiryAndClosingInvalidatePendingApproval()
    {
        using var certificate = PairingTestCertificates.Create();
        var clock = new PairingTestClock();
        var registry = new SqliteDeviceRegistry(Database, clock);
        var pairing = new PairingCoordinator(registry, clock);
        var request = PairingTestCertificates.Request(certificate, pairing.IssueChallenge().Token);
        var id = Guid.Parse(pairing.Submit(request).RequestId);
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.Null(pairing.PendingApproval());
        Assert.False(pairing.Approve(id));
        Assert.Equal("expired", pairing.GetStatus(id, PairingTestCertificates.StatusProof(certificate, id)).Status);
        Assert.Empty(registry.List());
        pairing.ClosePairing();
        Assert.Throws<PairingRejectedException>(() => pairing.GetStatus(id, PairingTestCertificates.StatusProof(certificate, id)));
    }

    [Fact]
    public void CancelledAndInvalidRequestsDoNotConsumeChallenge()
    {
        using var certificate = PairingTestCertificates.Create();
        var pairing = new PairingCoordinator(new SqliteDeviceRegistry(Database), TimeProvider.System);
        var request = PairingTestCertificates.Request(certificate, pairing.IssueChallenge().Token);
        Assert.Throws<PairingRejectedException>(() => pairing.Submit(request with { DisplayName = "Impostor" }));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => pairing.Submit(request, cancelled.Token));
        Assert.Equal("pending", pairing.Submit(request).Status);
    }

    [Fact]
    public async Task ApprovalAndDenialRaceCannotProduceContradictoryAuthorization()
    {
        using var certificate = PairingTestCertificates.Create();
        var registry = new SqliteDeviceRegistry(Database);
        var pairing = new PairingCoordinator(registry, TimeProvider.System);
        var id = Guid.Parse(pairing.Submit(PairingTestCertificates.Request(certificate, pairing.IssueChallenge().Token)).RequestId);
        var outcomes = await Task.WhenAll(Task.Run(() => pairing.Approve(id)), Task.Run(() => pairing.Deny(id)));
        Assert.Single(outcomes, result => result);
        var status = pairing.GetStatus(id, PairingTestCertificates.StatusProof(certificate, id)).Status;
        Assert.Equal(status == "approved", registry.Authorize(certificate) is not null);
    }

    [Fact]
    public void ActiveIdentityCannotBeOverwrittenAndRevokedKeyStaysRejectedAfterRepair()
    {
        using var oldKey = PairingTestCertificates.Create();
        using var newKey = PairingTestCertificates.Create();
        var registry = new SqliteDeviceRegistry(Database);
        var pairing = new PairingCoordinator(registry, TimeProvider.System);
        var deviceId = Guid.NewGuid();
        Guid Submit(System.Security.Cryptography.X509Certificates.X509Certificate2 certificate) =>
            Guid.Parse(pairing.Submit(PairingTestCertificates.Request(certificate, pairing.IssueChallenge().Token, deviceId)).RequestId);
        Assert.True(pairing.Approve(Submit(oldKey)));
        Assert.False(pairing.Approve(Submit(newKey)));
        Assert.NotNull(registry.Authorize(oldKey));
        Assert.Null(registry.Authorize(newKey));
        Assert.True(registry.Revoke(deviceId));
        Assert.True(pairing.Approve(Submit(newKey)));
        Assert.Null(registry.Authorize(oldKey));
        Assert.NotNull(registry.Authorize(newKey));
        Assert.Single(registry.List());
    }

    [Fact]
    public void CertificateExpiryIsRecheckedAfterRegistration()
    {
        using var certificate = PairingTestCertificates.Create();
        var clock = new PairingTestClock();
        var registry = new SqliteDeviceRegistry(Database, clock);
        var pairing = new PairingCoordinator(registry, clock);
        var id = Guid.Parse(pairing.Submit(PairingTestCertificates.Request(certificate, pairing.IssueChallenge().Token)).RequestId);
        Assert.True(pairing.Approve(id));
        Assert.NotNull(registry.Authorize(certificate));
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Null(registry.Authorize(certificate));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
