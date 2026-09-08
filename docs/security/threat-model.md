# Threat model

Status: design and implementation review in progress, not a completed security certification or physical-device acceptance.

| Actor | Asset / attack | Required mitigation and evidence |
| --- | --- | --- |
| Same-Wi-Fi stranger | Sniffing, forged mDNS, fake PC | TLS; QR-originated SPKI pin; mutual certificate authentication; real TLS rejection tests |
| Malicious paired device | Traverse outside share, ADS, junction/symlink replacement | Permission and share checks; strict path parser; handle-based Windows traversal; Windows junction/race integration tests |
| QR observer | Register a persistent attacker key | Single-use 120-second token, proof of possession, local PC approval, rate limit, no logged tokens |
| Previously allowed phone | Keep-alive TLS or cached certificate | Recheck authorization on every request; registry revocation is authoritative; real same-connection rejection test; durable uploads terminalized after local authorization revoke |
| Paired resource abuser | Disk/memory exhaustion, huge JSON, unbounded requests | Metadata/body/quota limits; per-device request and transfer bounds; global connection cap; cleanup ownership and active-transfer serialization |
| Malicious/buggy SAF provider | Path confusion, changed source between hash/upload, partial/corrupt export | Never convert content URI to OS path; validate display name separately; server verifies upload SHA-256; Android verifies download length + strong SHA-256 ETag; provider failures never report success |
| Crash or network loss | Partial file appears complete, wrong offset | Private staging, hash verification and atomic Windows no-overwrite completion; Android Foreground Service ownership; PR #11 adds durable offset/identity reconciliation before API readiness; physical power-loss acceptance remains required |
| Shared text sender | Credential disclosure or malicious URL | Opt-out history, restricted local storage, no text logs, explicit http/https open action |

## Trust boundaries

Local Windows account controls shares and approvals. A local administrator, kernel compromise, compromised Android OS and malware already acting as the logged-in Windows user are outside the protection boundary. This does not excuse reparse-point attacks in configured shares: remote paired devices never receive OS-path selection or link-creation APIs.

Private Windows state belongs under per-user LocalApplicationData with explicit current-user ACL. Keys use CurrentUser certificate store backed by non-exportable CNG keys. A share's private staging directory has restricted current-user ACL and is hidden from API listing. Android backup is disabled; signing keys remain in Android Keystore. Android SAF URIs are capabilities and stay inside `ContentResolver`; the app does not resolve them to filesystem paths. Tokens, plaintext histories and filenames must not enter structured operational logs. ASP.NET Core routine request diagnostics are filtered below Warning because file paths are carried in query strings.

## Current basic-file API gate

The basic Windows file API is enabled only on the authenticated mTLS listener. Its filesystem access is exclusively through the handle-safe adapter from PR #7. Network paths pass the domain syntax boundary and are opened one component at a time relative to retained directory handles. The API exposes an opaque logical share ID, not the physical root path.

Before PR #11, uploads were process-local. In PR #11 the production runtime uses a separate FULL/WAL SQLite journal and persistent staging capabilities: one request body is bounded to 4 MiB, staged privately, flushed before the SQLite committed offset advances, independently SHA-256 verified, and committed by same-volume no-replace rename. The rename is the commit point; post-commit cleanup failure cannot convert a committed file back to failed. Changing/clearing the configured share cancels an old upload before its next append/complete operation. Revoking a device denies subsequent requests and terminalizes its durable uploads after authorization revocation commits.

Downloads use a stable open handle and a strong SHA-256 ETag. Only a single open-ended byte range with matching `If-Match` is supported. An already-started request is not claimed to be a durable cancellation primitive; PR #11 defines per-transfer cancel and per-device revoke/commit ordering in the durable recovery contract.

## Android SAF / Foreground Service gate

Android file transfer is separated from `PairingRepository`. `FileTransferRepository` always builds a pinned-mTLS client from the saved endpoint/SPKI pin and Android Keystore client identity. Remote paths are syntactically validated before requests, but Windows handle containment remains the authority.

Upload sources come from `ACTION_OPEN_DOCUMENT`. Because v1 requires total size and SHA-256 up front, the source is streamed once to compute both, then the same URI is reopened for upload. No seek or filesystem-path conversion is used. If the provider changes bytes between passes, server-side SHA-256 completion fails. Ambiguous PATCH/complete response loss is reconciled against the process-local transfer status before retry/cancel decisions, avoiding blind duplicate chunk writes.

Download destinations come from `CREATE_DOCUMENT`. The full response is written through `ContentResolver`; Android checks Content-Length and recomputes the strong SHA-256 ETag before reporting completion. Failure triggers best-effort truncation, but arbitrary SAF providers do not guarantee atomic rollback, so physical provider tests remain a release gate.

Long-running file I/O is owned by a non-exported `dataSync` Foreground Service. Activity/ViewModel only select capabilities, start/cancel the service, and observe process-local status. A hard Android process kill is not durable recovery and can still leave Windows private staging until later reconciliation.

## Remaining release gates

Before claiming a transfer-capable MVP: complete PR #11 verification and execute physical Windows/Android upload/download/interruption tests including malicious metadata, junction replacement, SAF provider failure, Android process kill, notification denied/allowed behavior, dataSync timeout, Windows sleep/recovery, ReFS/mounted-volume behavior and multi-GB files. ACTION_SEND/MULTIPLE and text/history remain later UX increments.

## PR #11 durable recovery audit boundary

The DB offset is the only restart progress authority. File tails never advance it. File write/flush failures are retryable only after handle truncate + flush succeeds. Ambiguous journal commits poison service availability instead of rolling file bytes back. Startup verifies volume/file IDs, protected owner-only ACL, root/share generation, destination parent and final size/hash; a same-named file cannot establish completion. Terminal cleanup cannot restore state or authorization. Former share roots and unsafe private objects remain quarantined. Bounded registry/orphan scan exhaustion reduces availability rather than containment. Existing filesystem adversarial tests are preserved; new tests cover recovery and journal failure boundaries. See [recovery design](../architecture/durable-transfer-recovery.md) for the full algorithm and residual operational limits.
