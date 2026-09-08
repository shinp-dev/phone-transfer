# Android durable transfer recovery

Updated: 2026-09-08

This document defines the Android process-kill recovery boundary that follows the Windows durable upload/recovery work merged in PR #11. The Windows transfer journal remains the authoritative server-side source of truth for upload state. Android persists only the local capability and intent needed to re-establish an upload safely after its process has died.

## Scope

This increment implements user-driven upload recovery after Android process death/restart.

- Uploads persist a local operation and stable idempotency key before source hashing or any server create.
- A stable idempotency key survives process death so a crash after server create but before the response/local transfer-ID checkpoint cannot create a duplicate upload.
- The server transfer ID and last observed committed offset are persisted after successful observations, but the server `GET /api/v1/transfers/{id}` result remains authoritative after restart.
- The SAF content URI stays Android-local and is never sent to Windows or converted to a filesystem path.
- Before more upload bytes are sent, a current persisted SAF read grant must exist, the saved PC identity must re-verify, the source must still have the same validated name/size/SHA-256, and the server transfer metadata/state must be compatible.
- A server transfer that is already provably `completed` may converge the local operation without reopening the source. Completion is already authoritative on Windows and does not depend on the Android URI remaining available.
- Interrupted downloads are surfaced but **not resumed to the same destination URI** in this increment. Generic SAF providers do not give Phone Transfer a portable proof that reopening an existing document will truncate/rewrite it with the semantics needed for crash-safe restart-from-zero. The user must cancel the interrupted download and start a fresh download with a newly selected destination.
- Recovery is user-driven when the app is reopened. It does not schedule unrestricted background resurrection after process death because Android foreground-service launch policy may forbid that in the background.

ACTION_SEND/MULTIPLE, multi-transfer queue UX, text/history and unattended scheduler policy remain out of scope.

## Local durable operation journal

Use one versioned, bounded document with a checked file-sync / atomic-rename / directory-sync commit in app-private storage, separate from paired-PC persistence.

The current UI/foreground-service model owns at most one operation, so the journal limit is deliberately **one** operation: either pending or a completed receipt. The next explicit new transfer atomically replaces that receipt; there is no history or queue. Multiple records or an unknown/corrupt document fail closed rather than creating a queue semantics that the service does not implement.

A record contains:

- operation UUID and kind (`upload` or `download`);
- saved PC device UUID;
- share UUID;
- remote directory/path;
- SAF URI string;
- whether a persistable grant was observed and checkpointed;
- upload idempotency UUID, persisted before any server create;
- optional upload source name, total size and SHA-256 after the first full source inspection;
- optional server transfer UUID after it is known;
- last locally observed committed offset;
- `cancelRequested` terminal intent;
- optional `completedFileName` terminal receipt (schema 2; schema 1 pending records remain readable);
- update timestamp.

The journal is not a second authority for server progress. It is a recovery hint and local capability record. A local offset greater than the server committed offset is inconsistent and must fail closed. A server offset greater than the local checkpoint is expected after a crash between server acknowledgement and the local journal commit and may be adopted.

`persistedGrant` in the JSON is not treated as proof by itself. Resume checks Android's current `persistedUriPermissions`. This also closes the crash gap where Android granted persistence but the process died before the journal flag could be updated.

Unknown schema versions, oversized documents, multiple pending operations, malformed UUIDs, invalid remote paths, inconsistent upload metadata or invalid offsets fail closed. The app does not silently discard an unreadable recovery journal and does not start a new transfer over it.

Updates to an existing operation are monotonic:

- `cancelRequested=true` never reverts to false;
- committed offset never moves backward;
- source identity cannot change after it is checkpointed;
- server transfer ID cannot change after it is checkpointed;
- a checkpointed persisted-grant flag cannot regress.

These rules prevent a stale in-flight checkpoint writer from undoing a concurrently persisted cancellation or replacing the identity of the operation being recovered.

## Upload ordering

Fresh upload:

1. persist the local operation and idempotency key;
2. attempt to acquire the SAF persistable read grant;
3. if acquired, checkpoint that fact; if the process dies between steps 2 and 3, restart detects the real grant from Android rather than trusting the stale flag;
4. hash/count the source and validate the display name;
5. persist source name/size/SHA-256;
6. POST transfer metadata with the persisted idempotency key;
7. persist the returned server transfer ID and observed committed offset;
8. reopen the URI, stream/discard exactly to the server offset, then PATCH chunks;
9. after each acknowledged PATCH, persist the observed offset;
10. complete on the server;
11. after a matching full-size `Completed` is observed, durably persist its receipt, publish completion, and release any persisted grant. A kill before notification replays the receipt at startup.

The local checkpoint is deliberately after the server acknowledgement. A crash before the local write can only make Android lag the server; it cannot make Android claim bytes the server did not commit.

A provider that does not allow a persistable read grant may still complete the initial foreground upload while its temporary grant remains valid, but that operation is not advertised as resumable after process death.

## Restart reconciliation

On app restart, an unfinished operation is surfaced rather than silently resumed in the background.

For an upload:

1. require the saved PC record to still exist;
2. re-verify the saved PC through pinned TLS/client identity and `/api/v1/info` device ID;
3. if source metadata is already persisted, query the existing server transfer or repeat create with the stable idempotency key before touching the URI;
4. require server file name/size/hash to match the persisted operation and reject server offset below the local observed offset;
5. if the server already reports a valid `completed` transfer, persist the completion receipt without reopening the Android source or sending bytes;
6. if the server is `cancelled`/`failed` or otherwise incompatible, converge terminally and never resume bytes;
7. if more bytes are needed, require the actual persisted SAF read grant to still exist;
8. re-open and re-hash the whole source and require the same validated filename, total size and SHA-256;
9. adopt a compatible server offset at or ahead of the local checkpoint;
10. resume from the server offset; a full-size active/verifying transfer retries complete.

If source metadata had not yet been checkpointed when the process died, Android must first have a usable persisted grant, inspect/hash the source, persist that identity, and only then create/recover the server transfer.

Before any resumed bytes are sent, the whole source is re-hashed. Android never trusts a persisted URI merely because the URI string is unchanged.

## Cancellation and failures

Explicit user cancellation is a durable terminal intent:

1. persist `cancelRequested=true` locally;
2. stop the running coroutine if present;
3. query/cancel the known server transfer, or recreate/query by the persisted idempotency key when the create response had been lost;
4. if completion won the server race, preserve `Completed` rather than falsely reporting `Cancelled`;
5. only after verified cancelled/failed convergence remove the local operation; completion instead persists a receipt. Release the SAF grant after terminal authority is recorded.

If the process dies after step 1, restart processing sees `cancelRequested` and performs cancellation cleanup only; it never resumes upload bytes. A stale chunk-checkpoint writer cannot clear that flag because operation merges are monotonic.

Retryable network/service failures keep the operation so the user can retry. Non-retryable source mismatch, permission loss, identity mismatch, server terminal failure or invalid durable metadata do not send more bytes. If server cancellation cannot be proven, the cancel intent remains durable rather than deleting the only recovery record.

System/service destruction and foreground-service timeout are not treated as user cancellation. They leave the durable operation for a later user-driven retry.

## Download interruption policy

Downloads do not have a Windows transfer journal row, and the existing protocol does not expose a byte-range download recovery contract. More importantly, generic SAF providers do not give the app a portable durable guarantee that reopening the same existing destination after process death will truncate it exactly before a restart-from-zero write.

Therefore this increment deliberately does **not** advertise interrupted downloads as resumable. The interrupted operation remains visible so it cannot be silently forgotten or overlap a new transfer. The user cancels that local interrupted operation, then starts a new download and chooses a destination again.

This is an availability limitation, not a false-success path: Phone Transfer does not claim crash-safe download continuation until provider-independent destination replacement/truncation semantics are designed and tested.

## Concurrency and bounds

The foreground service owns at most one active transfer and the durable journal holds at most one pending operation. This matches the current UI and notification/cancel model and avoids pretending that a multi-transfer queue exists.

No long hash/network/content-provider operation is performed while holding the journal synchronization monitor. Checked atomic commits contain only the bounded JSON document. Provider capability checks are outside the journal monitor. Initial operation identity is committed before the service accepts a subsequent cancel command; the coroutine cancellation handler is entered before dispatch to IO.

The operation merge rules are intentionally one-way for safety-critical fields so cancellation and acknowledged progress cannot be undone by stale coroutine state.

## Process-kill / restart failure matrix

| Kill/failure point | Required recovery |
| --- | --- |
| after local operation persist, before grant/hash | require a real surviving grant before source access; otherwise no byte resume |
| after persistable grant succeeds, before grant flag checkpoint | detect Android's real persisted permission; do not rely on the stale JSON flag |
| after source metadata persist, before POST | repeat POST with the same idempotency key |
| server accepted POST, local transfer ID not persisted | repeat POST; receive the same durable transfer |
| server PATCH committed, local checkpoint not persisted | GET shows server ahead; adopt server offset, then revalidate source before more bytes |
| local checkpoint persisted | GET must be at or ahead of local offset |
| server reports offset behind local | fail closed; never resend based only on local state |
| process dies while source is reopened/skipping | repeat full source validation and skip from byte zero to the server offset |
| server completes, local record still present | prove matching server `completed`; persist completion receipt without reopening source or duplicate PATCH |
| source name/size/hash changed | fail terminal; do not PATCH |
| persisted SAF grant lost before more bytes are needed | do not PATCH; converge cancellation when possible |
| PC removed or `/info` identity mismatch | do not resume bytes |
| server cancelled/failed | converge local operation terminal |
| user cancellation persisted then process dies | cancellation cleanup only; never resume |
| stale checkpoint races with persisted cancellation | merged journal keeps `cancelRequested=true` |
| cancel races with server completion | server `Completed` wins and is not mislabeled `Cancelled` |
| retryable network failure | retain operation for user retry |
| FGS timeout/system service destruction | retain operation; do not convert to user cancel |
| local journal corrupt/unknown/multiple records | block new transfers; do not discard or overwrite the journal |
| download process dies | surface interrupted download as non-resumable; user cancels and selects a new destination |

## Tests required before merge

At minimum automate:

- versioned journal encode/decode and bounds;
- one-pending-operation fail-closed bound;
- stable idempotency across simulated process restart;
- restart before and after source metadata persistence;
- server-create response loss with the same idempotency key;
- server offset ahead of local checkpoint is adopted;
- server offset behind local checkpoint fails closed;
- completed-after-crash converges without further PATCH;
- cancelled/failed server state does not resume;
- changed source metadata/hash blocks resume;
- durable `cancelRequested` blocks resume;
- stale checkpoint cannot erase a concurrent durable cancellation;
- committed offset/server identity/source identity cannot regress or change;
- repeated restart/reconciliation is stable;
- interrupted download is explicitly non-resumable in this increment.

CI must keep protocol generation, Windows tests, Android formatting/unit/lint/assemble green. OpenAPI should not change unless the existing transfer status contract proves insufficient.

## Physical acceptance still required

Automated JVM tests cannot prove behavior of every SAF provider or Android process manager. Real-device acceptance remains required for provider grant persistence, process kill, device reboot, screen-off, foreground-service timeout, large files, nonseekable/reopenable sources, network loss and providers that reject persistent grants.

The implementation should only be described as durable **upload** resume until those physical checks are complete. Interrupted download continuation remains intentionally out of scope.

## Final-audit corrections

- Journal reads recover legacy Android 9 `.bak` generations before testing absence. Reads do not interpret permission/I/O errors as an empty journal. Writes use checked file `fsync`, atomic rename and parent-directory `fsync`; an ambiguous commit poisons the store instance until restart. The Android implementation uses `Os.fsync` for directories. JVM tests exercise the same writer with a real temporary directory and injected sync/rename failures.
- Cancellation uses the production `UploadCancellationRecovery` coordinator. It commits intent before lookup or HTTP, re-verifies the PC, reuses the stable create key, checkpoints a rediscovered server ID and validates every response before terminal convergence. Missing PC and all unproven errors retain intent; `retryable=false` is never evidence of absence.
- Coroutine cancellation propagates unchanged through PC verification. Only explicit cancellation or a real terminal failure enters cancellation recovery. Its NonCancellable section has a 15-second overall budget and a 10-second per-call timeout. Body reading remains inside the cancellable HTTP callback, so deadline cancellation closes the active call as well as waiting for headers.
- Response validation checks the expected known transfer ID, filename, size, hash, legal state, bounded/monotonic offset and full-size verifying/completed state. The current DTO does not expose owner/share/full path: Windows ownership checks and the idempotent create contract continue to provide those bindings.
- All state-bus transitions are serialized. Startup publishes a journal snapshot only while it is still current, under the journal monitor; terminal states cannot be overwritten by stale resumable callbacks. Failed cleanup remains visible as cancel pending and blocks new transfers.
- A completed receipt survives process recreation even if notification never ran. It is replayed without opening the source. Cancellation and stale checkpoint writers cannot erase it. Only a new explicit transfer retires it atomically.
- Download resume is rejected by the service in addition to being disabled by the state bus and UI.

Added JVM tests cover backup-only restart, corrupt/oversized journals, checked commit failures, completion receipts and stale startup snapshots; the actual cancellation coordinator is exercised with a real journal and scripted remote failures/completion races, including execution from an already-cancelled coroutine and a bounded hung request. These are not substitutes for Android service/SAF/OS physical acceptance. No instrumentation-based process-kill test is claimed.
