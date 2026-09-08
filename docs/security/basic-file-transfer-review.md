# Basic file transfer security review

Date: 2026-09-08 JST

Scope: `feature/basic-file-transfer` against the current `main` after PR #8. This review covers the first authenticated Windows share/list/upload/download network paths. It does not certify durable resume/recovery or Android SAF.

## Boundary checks

- Network-supplied relative paths enter the existing `RelativeSharePath` syntax boundary and then `IShareFileSystem`; API/Host code does not reconstruct Windows paths with `Path.Combine` or prefix checks.
- The configured physical root is never returned by the file API. Share IDs are opaque random values scoped to the running service and rotate when the configured root changes.
- Handle-safe traversal, reparse/junction/symlink/hardlink rejection, stable reads, private staging and no-replace rename remain owned by the Windows filesystem adapter from PR #7.
- Upload ownership is bound to the authenticated paired `DeviceId`. A different paired device receives `TRANSFER_NOT_FOUND` for another device's transfer state/mutation routes.
- Pairing revocation remains authoritative in SQLite and is checked on every HTTP request. Revocation also cancels that device's active process-local upload records and attempts immediate private-staging cleanup.
- If the local receive-folder configuration is cleared or changed, an existing upload is cancelled before its next append or completion operation can write/commit through the old pinned session.

## Resource and privacy checks

- Kestrel request bodies are globally capped at one 4 MiB chunk plus one byte; transfer metadata is independently capped at 128 KiB.
- Active uploads are capped per device and against a total declared staging quota. The authenticated listener additionally caps concurrent connections and concurrent requests per device so parallel body buffering/downloads are bounded.
- Routine ASP.NET Core request diagnostics are filtered below Warning because query strings contain share-relative filenames. API errors expose typed codes/request IDs, not physical paths or native exception text.
- Listing work is bounded and private staging names are never exposed.

## Commit semantics

- A chunk is buffered within the request limit, written to private staging, flushed, and only then advances the in-memory committed offset.
- Completion independently hashes staged bytes and uses the handle-safe no-replace rename as the commit point.
- Once the kernel rename succeeds, a later private-session cleanup failure cannot rewrite the transfer from `completed` to `failed`; a private staging-directory remnant may remain for future startup cleanup.
- A hard process/OS crash can still leave private staging because durable transfer records and startup reconciliation are intentionally deferred. No restart-resume claim is made by this increment.

## Remaining acceptance / deferred risks

- Real Windows 11/ReFS/mounted-volume acceptance, real Android-to-Windows streaming, multi-GB behavior and disk-full behavior still require physical testing.
- In-flight request revocation is serialized safely for process-local uploads, but durable cancel/revoke/crash ordering remains part of the later state-machine increment.
- Large-file completion currently hashes synchronously under the simple process-local transfer lock. This is intentionally conservative for the basic increment; durable/background verification and recovery should be redesigned rather than layered into this lock.
- Android SAF, foreground-service lifetime, persistent transfer DB, committed-offset recovery/truncate, startup staging reconciliation and rename/DB crash reconciliation are not part of this branch.
