# Android durable transfer recovery

Updated: 2026-09-08

This document defines the Android process-kill recovery boundary that follows the Windows durable upload/recovery work merged in PR #11. The Windows transfer journal remains the authoritative server-side source of truth for upload state. Android persists only the local capability and intent needed to re-establish a transfer safely after its process has died.

## Scope

This increment implements user-driven recovery after Android process death/restart.

- Uploads persist a local operation before hashing or creating the server transfer.
- A stable idempotency key is persisted before the first POST so a crash after server create but before the response/local checkpoint cannot create a duplicate upload.
- The server transfer ID and last observed committed offset are persisted after successful responses, but the server `GET /api/v1/transfers/{id}` result remains authoritative after restart.
- The SAF content URI stays Android-local and is never sent to Windows or converted to a filesystem path.
- Resume is allowed only when a persistable SAF grant still exists, the saved PC identity re-verifies, the source still has the same name/size/SHA-256, and the server transfer metadata/state is compatible.
- Downloads remain stateless on Windows. A killed Android download may be retried from byte zero only when its destination URI grant survives; this increment does not claim arbitrary-provider random-access download continuation.
- Recovery is user-driven when the app is reopened. It does not schedule unrestricted background resurrection after process death, because Android foreground-service launch policy may forbid that in the background.

ACTION_SEND/MULTIPLE, multi-transfer queue UX, text/history and unattended scheduler policy remain out of scope.

## Local durable operation journal

Use one versioned, bounded `AtomicFile` document in app-private storage, separate from paired-PC persistence.

A record contains:

- operation UUID and kind (`upload` or `download`);
- saved PC device UUID;
- share UUID;
- remote directory/path;
- SAF URI string and required grant direction;
- whether the persistable grant was actually acquired;
- upload idempotency UUID, persisted before any server create;
- optional upload source name, total size and SHA-256 after the first full source inspection;
- optional server transfer UUID after it is known;
- last locally observed committed offset;
- `cancelRequested` terminal intent;
- update timestamp.

The journal is not a second authority for server progress. It is a recovery hint and capability record. A local offset greater than the server committed offset is inconsistent and must fail closed. A server offset greater than the local checkpoint is expected after a crash between server acknowledgement and the local AtomicFile update and may be adopted after the source is revalidated.

Unknown schema versions, oversized documents, duplicate operation IDs, malformed UUIDs, invalid remote paths, inconsistent upload metadata or invalid offsets fail closed. The app never deletes the journal merely to evade a bound.

## Upload ordering

Fresh upload:

1. acquire the SAF persistable read grant when the provider supports it;
2. persist the local operation and idempotency key;
3. hash/count the source and validate the display name;
4. persist source name/size/SHA-256;
5. POST transfer metadata with the persisted idempotency key;
6. persist the returned server transfer ID and observed committed offset;
7. reopen the URI, seek by streaming/discarding exactly to the server offset, then PATCH chunks;
8. after each acknowledged PATCH, persist the observed offset;
9. complete on the server;
10. after `Completed` is observed, remove the local operation and release the persistable grant.

The local checkpoint is deliberately after server acknowledgement. A crash before the local write can only make Android lag the server, never claim bytes the server did not commit.

## Restart reconciliation

On app restart, unfinished operations are surfaced as recoverable rather than silently resumed in the background.

For an upload:

1. require the saved PC record to still exist;
2. require the persisted read grant to still be present;
3. re-verify the saved PC through pinned TLS/client identity and `/api/v1/info` device ID;
4. re-inspect the SAF source and require the same validated filename, total size and SHA-256 when metadata had already been persisted;
5. if the server transfer ID is unknown, repeat POST with the persisted idempotency key; the durable Windows mapping must return the same transfer if it was previously created;
6. otherwise GET the persisted server transfer;
7. require server file name/size/hash to match the local operation;
8. reject server offset < local observed offset;
9. adopt server offset >= local observed offset;
10. `completed` finishes the local operation without sending bytes; `cancelled`/`failed` are terminal locally; active states may resume from the server offset; a full-size active transfer retries complete.

Before any resumed bytes are sent, the whole source is re-hashed. Android never trusts a persisted URI merely because the URI string is unchanged.

## Cancellation and failures

Explicit user cancellation is a durable terminal intent:

1. persist `cancelRequested=true` locally;
2. stop the running coroutine if present;
3. best-effort DELETE the known server transfer, or recreate/query by the persisted idempotency key if necessary;
4. only after cancellation has converged locally, remove the operation and release the SAF grant.

If the process dies after step 1, restart processing sees `cancelRequested` and never resumes upload bytes.

Retryable network/service failures keep the operation and persistable grant so the user can retry. Non-retryable source mismatch, permission loss, identity mismatch, server terminal failure or invalid durable metadata are surfaced as terminal and must not send more bytes.

System/service destruction and foreground-service timeout are not treated as user cancellation. They leave the durable operation for a later user-driven retry.

## Download recovery

Downloads do not have a Windows transfer journal row. A persisted download operation therefore contains only PC/share/path/destination capability state.

If Android dies during a download, reopening the app may offer a retry only if the persistable write grant still exists and PC identity re-verifies. The retry starts from byte zero and reuses the existing full-response SHA-256/length verification. This avoids claiming arbitrary SAF-provider seek/truncate guarantees that cannot be proven generically.

## Concurrency and bounds

The existing foreground service continues to own at most one active transfer at a time. The journal may retain a small bounded number of interrupted operations so a process kill cannot orphan their recovery intent. Starting/resuming one operation never mutates another record.

No long hash/network/content-provider operation is performed while holding the journal synchronization monitor. Atomic writes contain only the bounded JSON document.

## Process-kill / restart failure matrix

| Kill/failure point | Required recovery |
| --- | --- |
| after local operation persist, before source hash | re-inspect source, continue |
| after source metadata persist, before POST | repeat POST with same idempotency key |
| server accepted POST, local transfer ID not persisted | repeat POST; receive same durable transfer |
| server PATCH committed, local checkpoint not persisted | GET shows server ahead; adopt server offset after source revalidation |
| local checkpoint persisted | GET must be at or ahead of local offset |
| server reports offset behind local | fail closed; never resend based only on local state |
| process dies while source is reopened/skipping | repeat full source validation and skip from byte zero to server offset |
| server completes, local record still present | GET `completed`; remove local record without duplicate bytes |
| source name/size/hash changed | fail terminal; do not PATCH |
| persistable SAF grant lost | fail terminal; do not access the URI |
| PC removed or `/info` identity mismatch | fail terminal; do not resume |
| server cancelled/failed | converge local operation terminal |
| user cancellation persisted then process dies | cancellation cleanup only; never resume |
| retryable network failure | retain operation for user retry |
| FGS timeout/system service destruction | retain operation; do not convert to user cancel |

## Tests required before merge

At minimum automate:

- versioned journal encode/decode and bounds;
- stable idempotency across simulated process restart;
- restart before and after source metadata persistence;
- server-create response loss with the same idempotency key;
- server offset ahead of local checkpoint is adopted;
- server offset behind local checkpoint fails closed;
- completed-after-crash converges locally without PATCH;
- cancelled/failed server state does not resume;
- changed source metadata/hash blocks resume;
- lost persistable grant blocks resume;
- durable `cancelRequested` blocks resume;
- repeated restart/reconciliation is stable;
- download recovery is explicitly restart-from-zero, not unsupported random-access continuation.

CI must keep protocol generation, Windows tests, Android formatting/unit/lint/assemble green. OpenAPI should not change unless the existing transfer status contract proves insufficient.

## Physical acceptance still required

Automated tests cannot prove behavior of every SAF provider or Android process manager. Real-device acceptance remains required for provider grant persistence, process kill, device reboot, screen-off, foreground-service timeout, large files, nonseekable/reopenable sources, network loss and providers that reject persistent grants.
