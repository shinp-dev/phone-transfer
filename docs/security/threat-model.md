# Threat model

Updated: 2026-09-09 JST

Status: implementation/code review complete for the current MVP runtime boundary; physical-device acceptance is still pending. This is not a third-party security certification.

| Actor / failure | Asset / attack | Current mitigation / evidence |
| --- | --- | --- |
| Same-Wi-Fi stranger | sniffing, forged mDNS, fake PC | QR-originated SPKI pin; TLS; client certificate authentication; stable PC Device ID; mDNS is routing metadata only |
| QR observer | register an attacker key | single-use 120-second token; proof of private-key possession; explicit PC comparison-code approval; bounded pairing endpoint |
| Previously paired phone | keep access after revoke | paired-device registry is authoritative; authorization rechecked on every authenticated request; durable uploads are terminalized after authorization revoke |
| Malicious paired device | traversal, ADS, junction/symlink/reparse/hardlink, root replacement | strict relative-path parser; retained-handle Windows traversal; local NTFS/ReFS root constraints; identity/reparse checks; private staging; no-replace commit |
| Paired resource abuser | huge bodies, too many transfers/requests, disk exhaustion | request/body limits; per-device/global concurrency limits; active-transfer and staging quota bounds; bounded listing/recovery scans |
| Malicious/buggy SAF provider | changed source, fake metadata, permission loss, partial destination | SAF URI never becomes a filesystem path; full source hash/size; Windows independent hash; actual persisted URI grant checked on resume; download length/ETag validation; failures never report partial output as complete |
| Android process kill | lost client state, duplicate bytes, stale cancel | durable local operation/idempotency/source/server checkpoint; Windows status remains authority; full source recheck; stale local state cannot clear durable cancel; completion receipt survives process recreation |
| Windows process/OS crash | uncommitted tail, ambiguous DB commit, rename/DB gap | SQLite durable journal; write/flush before committed offset; ambiguous-commit read-back/poison; startup reconciliation before API readiness; destination identity/volume/size/hash proof after rename gap |
| Network response loss | duplicate create/chunk/complete/text presentation | stable idempotency for create/text; server-status reconciliation for transfer mutations; exact offset validation; bounded text duplicate-presentation window |
| Shared text sender | malicious URL / credential leakage | `TextSend` permission; bounded content; URL kind is explicit HTTP/HTTPS without credentials; tray body omitted; no auto-open; PC user must explicitly open after re-validation |

## Trust boundaries

The logged-in Windows account controls local share selection and pairing approval. A local administrator, kernel compromise, compromised Android OS and malware already acting as the logged-in Windows user are outside the application protection boundary.

This does **not** relax remote containment. A paired device never receives a physical Windows root path or an API to create links. All remote file paths remain logical relative paths and are revalidated by the Windows handle-safe adapter.

## Identity and transport

Windows owns two HTTPS listeners on the selected private IPv4 interface:

- pairing/bootstrap: port 58442;
- authenticated mTLS API: port 58443.

The pairing QR contains the server SPKI pin and endpoint metadata. Android does not fall back to a public CA for the paired PC. The saved endpoint can move after DHCP/Wi-Fi changes only after the existing SPKI pin, Android client identity and `/api/v1/info` stable Device ID verify against a discovered candidate.

Windows client-certificate validation yields a paired-device record, and that authorization is checked again at application-request time so a cached/reused TLS session does not preserve revoked authority.

## Windows file containment and durability

Configured shares are local NTFS/ReFS folders only. UNC, volume-root, application-state overlap and unsafe reparse ancestry are rejected during configuration.

Remote traversal opens path components relative to retained directory handles and checks volume/file identity and reparse/hardlink constraints. Private staging is owner-restricted. Upload completion is a same-volume no-overwrite rename; the rename is the file commit point.

Windows upload progress is authoritative in a separate durable SQLite journal. A successful chunk response means bytes were written/flushed and the corresponding committed offset was durably accepted. Ambiguous journal commits are accepted only after exact read-back proof; otherwise the service stops serving mutations until restart reconciliation.

Startup reconciliation runs before the main API listener becomes ready. It may truncate uncommitted staging tails to a proven durable offset. A rename-before-DB crash becomes Completed only when the expected destination is proven by persisted identity/volume/size/SHA-256 evidence. Unproven objects are failed/quarantined rather than guessed safe.

## Android SAF and process-kill recovery

Android file transfer is separated from pairing/discovery ownership and always uses the saved SPKI pin plus Keystore client identity.

SAF `content://` URIs remain local capabilities and are never sent as Windows paths. Upload source bytes are fully inspected before create. The stable local operation ID, idempotency key and source identity are persisted before server create; server transfer ID/offset observations are checkpointed monotonically after server acknowledgement.

On process restart, upload resume requires:

- saved PC identity re-verification;
- an actual persisted SAF read grant;
- full source name/size/SHA-256 match;
- server transfer ownership/metadata/state match.

Windows status remains progress authority. Server-ahead/local-behind can be adopted after a crash gap; server-behind/local-ahead fails closed. Server Completed can converge without reopening the Android source.

User cancel intent is durable before remote cancellation. A stale checkpoint cannot erase it. If cancel races with a server-side Completed state, Completed wins. If remote cancellation cannot be proven, the local cancel record remains for later recovery rather than being silently discarded.

Interrupted downloads are intentionally not resumed to the same generic SAF destination after Android process death. A partial provider-owned destination can remain if the provider refuses truncation, but it is never reported as completed.

## Text / URL

The currently shipped direction is Android -> PC. The authenticated text endpoint requires `TextSend` and validates bounded `plainText`/`url` content.

URL receipt does not execute content. Only absolute HTTP/HTTPS without embedded credentials is accepted as URL kind, and the Windows user must press an explicit open button. Tray notifications are generic and omit the body.

User-visible persistent history and PC -> Android delivery are not current product features. The process-local idempotency window prevents ordinary response-loss duplicate presentation during the active Windows runtime but is not a durable message queue.

## Privacy / local state

- Windows private state lives under the current user's LocalApplicationData and sensitive staging/state uses owner-restricted access where required by the storage adapter.
- Windows server private key is current-user CNG and non-exportable.
- Android backup is disabled; private keys remain in Android Keystore.
- Routine ASP.NET request diagnostics are suppressed below Warning because query strings can carry logical filenames.
- Pairing tokens, certificate material, SAF URIs, text bodies and private filenames should not be added to operational logs/test evidence unnecessarily.

## Remaining release evidence

Before claiming the physical MVP gate complete, execute [physical-device acceptance](../physical-device-acceptance.md), including pairing/mDNS, both file directions, text/URL, Android process-kill upload recovery, Windows process restart, interrupted-download fail-closed behavior and network interruption. Extended acceptance covers device/PC reboot, sleep/screen-off/FGS behavior, provider variants, multi-GB/disk-full and NTFS/ReFS/mounted-volume behavior.

The repository also retains non-blocking release/operations debt such as unprotected `main`, tag-based rather than immutable Action references, optional packaging/signing polish and deliberately deferred journal retention/maintenance. See [final audit](../final-audit.md).
