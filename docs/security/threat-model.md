# Threat model

Status: design review, not a completed security certification or Windows filesystem audit.

| Actor | Asset / attack | Required mitigation and evidence |
| --- | --- | --- |
| Same-Wi-Fi stranger | Sniffing, forged mDNS, fake PC | TLS; QR-originated SPKI pin; mutual certificate authentication; real TLS rejection tests |
| Malicious paired device | Traverse outside share, ADS, junction/symlink replacement | Permission and share checks; strict path parser; handle-based Windows traversal; Windows junction/race integration tests |
| QR observer | Register a persistent attacker key | Single-use 120-second token, proof of possession, local PC approval, rate limit, no logged tokens |
| Previously allowed phone | Keep-alive TLS or cached certificate | Recheck authorization on every request; revoke before cancellation; real same-connection rejection test |
| Paired resource abuser | Disk exhaustion, huge JSON, unbounded requests | Metadata/body/quota limits; bounded concurrency; retention; cleanup ownership and active-transfer locks |
| Crash or network loss | Partial file appears complete, wrong offset | Private staging, durable offsets, hash verification, atomic no-overwrite completion and recovery reconciliation |
| Shared text sender | Credential disclosure or malicious URL | Opt-out history, restricted local storage, no text logs, explicit http/https open action |

## Trust boundaries

Local Windows account controls shares and approvals. A local administrator, kernel compromise, compromised Android OS and malware already acting as the logged-in Windows user are outside the protection boundary. This does not excuse reparse-point attacks in configured shares: remote paired devices must never gain OS-path selection or link-creation APIs.

Private Windows state belongs under per-user LocalApplicationData with explicit current-user ACL. Keys use CurrentUser certificate store backed by non-exportable CNG keys. A share's private staging directory needs restricted ACL and hidden-from-API treatment. Android backup is disabled; signing keys remain in Android Keystore. Tokens, plaintext histories and filenames must not enter structured operational logs.

## Release gates

No transfer listener is enabled in the Phase 1 tray. Before enabling: implement pairing proof and approval; current certificate authorization; actual Windows handle containment; quotas; cancellation/cleanup serialization. Before claiming MVP: execute full upload/download/interruption/resume tests, invalid/expired certificates and tokens, malicious metadata, Windows junction replacement, SAF provider failure, Android process kill and PC sleep/recovery.
