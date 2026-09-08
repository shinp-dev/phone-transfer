# Durable transfer recovery design

Updated: 2026-09-08

This note defines the recovery boundary for the next Phase 3 increment. It is intentionally stricter than the current process-local upload registry. The existing mTLS, per-request revocation, handle-safe filesystem containment, private staging and no-replace completion remain mandatory lower-layer invariants.

## Scope and split

Do not implement Windows and Android recovery as one giant change.

1. **Windows durable upload journal and staging recovery**
   - persist transfer metadata, ownership, idempotency and committed offsets;
   - reopen private staging after process restart without reconstructing attacker-controlled paths;
   - reconcile file length against the durable committed offset;
   - recover safely across disk-full, cancellation/revocation and crash during completion;
   - preserve current authenticated API semantics and keep downloads handle-stable.
2. **Android durable operation persistence and user-driven resume**
   - persist local operation metadata and server transfer ID/offset;
   - resume only when the SAF URI grant survives and the same saved PC identity still verifies;
   - do not attempt automatic background restart where Android FGS policy does not permit it.

The first implementation PR should be Windows-only plus protocol changes only if the existing wire contract is insufficient. Android recovery should follow after the Windows recovery contract is stable.

## Non-negotiable invariants

- The SQLite committed offset is the durable authority. It may advance **only after** the corresponding staging bytes have been flushed to stable storage.
- Recovery may truncate uncommitted tail bytes, but must never invent or advance bytes that the journal did not commit.
- A destination file is never overwritten. The existing same-volume handle-relative no-replace rename remains the commit primitive.
- A successful rename is the file commit point. A later database failure must never cause the committed destination to be deleted or rewritten as a failed upload.
- Network inputs never provide physical paths, native handles, staging names or staging tokens.
- Persistent staging objects are reopened only through the existing retained-root/relative-handle containment model and must pass the same reparse/hardlink/volume checks as normal file access.
- Device revocation is authoritative before cleanup. Cleanup failure cannot restore access.
- Share reconfiguration invalidates resumption. Old staging may be cleaned up best-effort through a locally persisted root snapshot, but it is never served from an unshared root.
- Terminal/idempotency records are retained long enough for retries and ambiguous responses to resolve consistently; eviction may remove only terminal records.

## Windows persistence model

Use a dedicated `transfers.db` rather than adding transfer writes to `devices.db`. Authentication reads must not contend on a high-frequency transfer journal.

A durable record needs at least:

- `transfer_id` — random GUID, primary key;
- `owner_device_id` — authenticated paired device;
- `idempotency_key` — unique with owner device;
- destination relative path;
- total size and expected lowercase SHA-256;
- durable state;
- committed byte count;
- configured-share root snapshot or local-only configuration identity used to reject resume after share change;
- random local-only staging token/name, never accepted from the network;
- staging volume/file identity captured before completion when available;
- created/updated timestamps and optional terminal reason.

Use WAL, `synchronous=FULL`, a bounded busy timeout and explicit transactions. Keep this database separate from authorization so disk-heavy transfer traffic cannot block TLS/request authorization.

## Durable staging capability

The current staging directory is process-owned and intentionally never adopted. Durable recovery requires a new explicit capability instead of weakening that rule globally.

Add a narrow durable-staging API to the filesystem boundary. It should support operations conceptually equivalent to:

- create a private durable staging object and return an opaque local token;
- reopen that exact object from the token under the configured root;
- read length and bytes;
- write at an exact offset;
- flush to disk;
- truncate to a committed offset and flush;
- capture stable file identity needed for completion reconciliation;
- complete with the existing handle-relative no-replace rename;
- abandon/delete by handle.

Do not expose a raw path or `SafeFileHandle` above Infrastructure. Do not make the ordinary `CreateStaging` silently adopt old private directories.

A durable private staging directory/file must be reopened by a locally generated high-entropy name stored in `transfers.db`, opened relative to the retained root handle with `OBJ_DONT_REPARSE`, and revalidated for volume, reparse status, directory/file kind and single-link stability. Existing private-name hiding in normal listing remains in force.

## PATCH ordering and crash recovery

For each upload chunk:

1. load and lock the durable transfer record;
2. require owner device, nonterminal state and exact request offset;
3. reopen/validate staging capability;
4. if the actual file is longer than the durable committed offset, truncate it back to the committed offset before accepting new bytes;
5. if the actual file is shorter than the durable committed offset, fail closed because acknowledged bytes are missing;
6. write the chunk at the durable committed offset;
7. flush staging to disk;
8. persist the new committed offset/state in SQLite with a full durable commit;
9. only then return the advanced offset to the client.

This deliberately permits the crash case “file contains more bytes than DB”. Recovery truncates that tail. The opposite case “DB claims bytes that are missing from the file” is corruption and must not be resumed.

## Disk-full behavior

Disk-full should not destroy a previously valid resumable upload merely because one new chunk failed.

- The durable offset remains unchanged until a successful flush + DB commit.
- If a write partially extends the file and then fails, truncate back to the durable offset before returning.
- If rollback/truncate succeeds, surface a typed retryable storage-full/write error and leave the transfer resumable/paused.
- If rollback itself cannot be proven, mark the transfer failed and never advance the offset.

Tests must inject a partial write followed by disk-full/failure rather than only a pre-write exception.

## Create ordering and orphan cleanup

Create the private staging object first, then insert the durable transfer record. A crash between those steps can leave an unreferenced private staging object. Startup reconciliation must delete private staging objects not referenced by the journal, subject to the same handle-safe validation.

If a journal record exists but its staging object is missing or invalid, mark it failed/cancelled; never recreate empty staging under the same transfer and pretend prior committed bytes still exist.

The idempotency key must be stored durably so a retry after server restart returns the same transfer record rather than creating a second upload.

## Completion and rename/DB crash

Completion is the hardest transition.

1. require durable committed bytes == total size;
2. persist a verifying/commit-intent state;
3. reopen staging and verify length + SHA-256;
4. capture stable staging file identity immediately before rename;
5. persist the identity/commit intent if needed for recovery;
6. perform the existing same-volume no-replace handle rename;
7. treat successful rename as the irreversible file commit point;
8. persist `Completed` after rename.

A crash after step 6 but before step 8 must be recoverable without guessing. On startup, if the record is in commit-intent/verifying state and the staging object is gone, open the destination through the handle-safe filesystem and compare it against the recorded staging file identity plus expected size/hash. Only an exact identity/integrity match may be promoted to `Completed`.

If the destination exists but identity does not match, do **not** delete or overwrite it and do not report success. Mark the transfer conflicted/failed for operator/client retry under a new name.

If the staging object still exists and no destination commit is proven, completion may be retried through the normal no-replace primitive.

## Cancellation, revocation and share changes

Persist terminal cancellation before attempting staging deletion. Cleanup is retryable housekeeping after authorization/state has already been revoked.

On startup, cancelled/failed records may retain private staging only long enough for cleanup retries. They are never reopened for network writes.

If the configured share changed since transfer creation, the transfer cannot resume. Mark it cancelled/failed and attempt cleanup against the locally persisted old root through the same handle-safe opener. If that old root is now unsafe, leave the private remnant untouched rather than bypass containment.

## Concurrency

Do not keep the current single global service lock around long hash/file operations. Durable persistence introduces I/O long enough that one transfer must not block every device.

Use a per-transfer serialization primitive plus short database transactions. Global accounting (record cap/staging quota/idempotency creation) can use a short coordination lock/transaction. Revocation must be able to mark all of one device’s transfers cancelled without waiting behind unrelated devices.

Recovery runs before the API starts accepting file-transfer requests, or behind an explicit readiness gate that rejects file routes until reconciliation completes.

## Startup reconciliation matrix

At minimum cover these states:

| Journal | Staging | Destination | Recovery |
| --- | --- | --- | --- |
| active, offset N | length N | absent | resumable |
| active, offset N | length > N | absent | truncate to N, resumable |
| active, offset N | length < N | absent | fail closed |
| active | missing/invalid | absent | fail closed |
| verifying/commit-intent | present | absent | reverify, retry completion |
| verifying/commit-intent | missing | exact committed identity | mark completed |
| verifying/commit-intent | missing | unrelated destination | conflict/fail, never delete destination |
| completed | absent | committed destination | keep completed |
| cancelled/failed | present | any | cleanup only |
| no journal record | private staging present | any | orphan cleanup |

Every row needs automated tests where practical. The rename→DB-commit crash case is a required test seam, not a comment-only scenario.

## Android follow-up boundary

After Windows durability is merged, Android can persist one or more local transfer operations. The persisted state may include the server transfer ID and committed offset, but the SAF URI remains local-only.

Resume requires:

- the saved PC still exists and pinned mTLS/client identity verifies;
- the persisted URI grant is still usable;
- remote path/filename validation still passes;
- server `GET transfer` ownership and offset agree with the local operation;
- upload source metadata/hash still matches the original transfer before sending more bytes.

If a provider did not grant persistable URI access, process-kill resume is unavailable for that operation; surface that explicitly rather than copying an arbitrarily large source into app-private storage.

## Required tests before merge

- restart after create, after each PATCH ordering boundary and during complete;
- committed DB offset vs shorter/longer staging file;
- partial write + disk-full + rollback success/failure;
- idempotent create retry after restart;
- rename succeeds then DB completion write is interrupted;
- destination race/no-replace conflict during recovery;
- revoke/cancel while append or complete is in flight;
- share reconfiguration while a durable transfer exists;
- orphan private staging cleanup;
- hostile reparse/junction/hardlink replacement attempts against recovered staging;
- quota/count reconstruction from persisted nonterminal records.

Do not claim durable resume until these crash/recovery tests exist and the recovery implementation has had an independent security/state-machine audit.
