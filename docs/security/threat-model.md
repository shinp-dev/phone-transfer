# Threat model

Status: design and implementation review in progress, not a completed security certification or physical-device acceptance.

| Actor | Asset / attack | Required mitigation and evidence |
| --- | --- | --- |
| Same-Wi-Fi stranger | Sniffing, forged mDNS, fake PC | TLS; QR-originated SPKI pin; mutual certificate authentication; real TLS rejection tests |
| Malicious paired device | Traverse outside share, ADS, junction/symlink replacement | Permission and share checks; strict path parser; handle-based Windows traversal; Windows junction/race integration tests |
| QR observer | Register a persistent attacker key | Single-use 120-second token, proof of possession, local PC approval, rate limit, no logged tokens |
| Previously allowed phone | Keep-alive TLS or cached certificate | Recheck authorization on every request; registry revocation is authoritative; real same-connection rejection test; active process-local upload staging cancelled on local revoke |
| Paired resource abuser | Disk/memory exhaustion, huge JSON, unbounded requests | Metadata/body/quota limits; per-device request and transfer bounds; global connection cap; cleanup ownership and active-transfer serialization |
| Crash or network loss | Partial file appears complete, wrong offset | Private staging, hash verification and atomic no-overwrite completion in the basic API; durable offsets/startup reconciliation remain required before restart-resume claims |
| Shared text sender | Credential disclosure or malicious URL | Opt-out history, restricted local storage, no text logs, explicit http/https open action |

## Trust boundaries

Local Windows account controls shares and approvals. A local administrator, kernel compromise, compromised Android OS and malware already acting as the logged-in Windows user are outside the protection boundary. This does not excuse reparse-point attacks in configured shares: remote paired devices never receive OS-path selection or link-creation APIs.

Private Windows state belongs under per-user LocalApplicationData with explicit current-user ACL. Keys use CurrentUser certificate store backed by non-exportable CNG keys. A share's private staging directory has restricted current-user ACL and is hidden from API listing. Android backup is disabled; signing keys remain in Android Keystore. Tokens, plaintext histories and filenames must not enter structured operational logs. ASP.NET Core routine request diagnostics are filtered below Warning because file paths are carried in query strings.

## Current basic-file API gate

The basic Windows file API is enabled only on the authenticated mTLS listener. Its filesystem access is exclusively through the handle-safe adapter from PR #7. Network paths pass the domain syntax boundary and are opened one component at a time relative to retained directory handles. The API exposes an opaque logical share ID, not the physical root path.

Uploads are process-local and deliberately non-durable: one request body is bounded to 4 MiB, staged privately, flushed before the in-memory committed offset advances, independently SHA-256 verified, and committed by same-volume no-replace rename. The rename is the commit point; post-commit cleanup failure cannot convert a committed file back to failed. Changing/clearing the configured share cancels an old upload before its next append/complete operation. Revoking a device denies subsequent requests and cancels its active process-local upload staging.

Downloads use a stable open handle and a strong SHA-256 ETag. Only a single open-ended byte range with matching `If-Match` is supported. An already-started request is not claimed to be a durable cancellation primitive; full revoke/cancel/crash ordering belongs to the later transfer state-machine work.

## Remaining release gates

Before claiming a transfer-capable MVP: add Android SAF/foreground-service ownership; implement persistent transfer records, committed-offset recovery/truncate, startup staging reconciliation, crash-between-rename-and-DB reconciliation and disk-full recovery; execute physical Windows/Android upload/download/interruption tests including malicious metadata, junction replacement, SAF provider failure, Android process kill, Windows sleep/recovery, ReFS/mounted-volume behavior and multi-GB files.
