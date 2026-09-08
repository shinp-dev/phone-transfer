using PhoneTransfer.Domain;

namespace PhoneTransfer.Application.Files;

public sealed partial class DurableFileTransferService
{
    private void Reconcile()
    {
        foreach (var entry in transfers.Values.OrderBy(e => e.Record.CreatedAt))
        {
            Reconcile(entry);
            Hit(TransferFaultPoint.ReconciliationStep);
        }
        var configuration = configurations.Read();
        if (configuration is null) return;
        RequireGeneration(configuration);
        using var session = fileSystem.OpenDurable(configuration);
        // Preserve every referenced capability, including terminal and old-share records. Cleanup only
        // unreferenced app-layout objects. Unsafe objects are left hidden, never followed or adopted.
        session.CleanupOrphans(transfers.Values.Select(e => e.Record.Staging.DirectoryToken).ToHashSet());
    }

    private void Reconcile(Entry entry)
    {
        var record = entry.Record;
        if (record.State is TransferState.Cancelled or TransferState.Failed)
        {
            Cleanup(record);
            return;
        }
        var configuration = configurations.Read();
        if (configuration is null || configuration.Generation != record.ShareId)
        {
            if (!record.Terminal) Save(entry, TransferState.Cancelled);
            return; // Old share is quarantined; no path snapshot is reopened.
        }
        try
        {
            using var session = fileSystem.OpenDurable(configuration);
            if (session.RootIdentity != record.Staging.RootIdentity)
            {
                if (record.State == TransferState.Completed) throw new TransferJournalException();
                Fail(entry);
                return;
            }
            if (record.State == TransferState.Completed)
            {
                // A completed receipt never reuses staging. Missing/replaced completed destination is
                // an operational integrity failure: keep terminal DB authority and refuse API readiness.
                if (!session.VerifyDestination(record.Staging, RelativeSharePath.Parse(record.Destination), record.TotalSize, record.Sha256))
                    throw new TransferJournalException();
                try { session.CleanupEmptyDirectory(record.Staging); }
                catch (IOException) { /* Completed remains authoritative. */ }
                return;
            }
            if (record.State == TransferState.Verifying)
            {
                // Check the proven rename FIRST, even after revocation: an already committed file
                // cannot be undone. Device authorization remains revoked independently.
                var committed = false;
                try { committed = session.VerifyDestination(record.Staging, RelativeSharePath.Parse(record.Destination), record.TotalSize, record.Sha256); }
                catch (IOException) { /* No proven destination; require valid original staging below. */ }
                if (committed)
                {
                    Save(entry, TransferState.Completed);
                    try { session.CleanupEmptyDirectory(record.Staging); }
                    catch (IOException) { /* Completed remains authoritative. */ }
                    return;
                }
            }
            if (!IsAuthorized(record))
            {
                Save(entry, TransferState.Cancelled);
                Cleanup(entry.Record);
                return;
            }
            using (var staging = session.Reopen(record.Staging, RelativeSharePath.Parse(record.Destination)))
                Normalize(staging, record.CommittedOffset);
            if (record.State == TransferState.Verifying) CompleteStaging(entry, configuration);
            else if (record.State != TransferState.Paused) Save(entry, TransferState.Paused);
        }
        catch (TransferJournalException) { throw; }
        catch (BasicFileTransferException) when (!poisoned)
        {
            Fail(entry);
            Cleanup(entry.Record);
        }
        catch (IOException)
        {
            if (record.State == TransferState.Completed) throw new TransferJournalException();
            Fail(entry);
            Cleanup(entry.Record);
        }
    }

    private void Cleanup(DurableTransfer record)
    {
        if (record.State is not (TransferState.Failed or TransferState.Cancelled)) return;
        try
        {
            var configuration = configurations.Read();
            if (configuration is null || configuration.Generation != record.ShareId) return;
            using var session = fileSystem.OpenDurable(configuration);
            using var staging = session.Reopen(record.Staging, RelativeSharePath.Parse(record.Destination));
            staging.Delete();
        }
        catch (IOException) { /* Never resurrect terminal records; retry bounded cleanup at next startup. */ }
    }
}
