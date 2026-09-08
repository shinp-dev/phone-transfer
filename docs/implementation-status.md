# Implementation status

Updated: 2026-09-08

See [development handoff](handoff.md) for continuation order. The repository is still pre-MVP: pairing, mDNS, shared-folder configuration, Windows handle-safe filesystem, the first authenticated Windows file-transfer API, and Android SAF/Foreground Service transfer ownership are implemented. Durable restart resume/recovery and full physical-device acceptance are not yet implemented.

## Phase 1 — complete

Implemented: monorepo, layer boundaries, tray/Compose startup shells, OpenAPI wire schemas and deterministic DTO generation, CI definitions, domain path syntax/state/offset/permission rules, stream digest verifier, stable device identity adapter, restricted Kestrel host factory and `/api/v1/info`, unit and real-TLS tests, ADRs and threat model.

## Phase 2 — pairing and mDNS implemented; physical acceptance pending

The Windows backend includes single-use 120-second QR challenges, ECDSA P-256 proof verification, local comparison-code approval, signature-authorized status polling, isolated HTTPS pairing, SQLite device registration/revocation, current-user non-exportable CNG server identity, request-by-request revocation checks and bounded shutdown.

Android includes QR validation, offline scanning, Keystore identity, pinned HTTPS registration, signed polling, comparison-code UI, mTLS `/api/v1/info` verification before atomic persistence, saved-PC endpoint persistence and local removal.

Production mDNS is wired: Windows advertises `_phone-transfer._tcp` through Windows DNS-SD on the selected private IPv4 interface; Android uses `NsdManager` with a multicast lock. Discovery is routing metadata only. A changed endpoint is saved only after the stored SPKI pin, client certificate and stable `/api/v1/info` Device ID all verify.

## Phase 3 — handle-safe Windows file API and Android basic transfer implemented

Windows can select, persist and clear one receive-folder root. Configuration accepts only existing local NTFS/ReFS folders, rejects UNC paths, volume roots, overlap with application data and reparse points in the configured ancestry.

PR #7 added the standalone handle-safe filesystem adapter: pinned root/traversal, bounded handle-based listing, stable file reads, private ACL-protected staging and atomic same-volume no-overwrite completion. Child components are opened relative to retained directory handles and reparse points/junctions/symlinks/hardlinks/replacement races fail closed. PR #7 final Windows CI passed 109 tests with 0 skipped.

PR #9 added the first authenticated network use of that adapter:

- `GET /api/v1/shares` exposes one logical share without returning the physical Windows root path; the share ID is an opaque random identifier scoped to the running service and is not derived from the root path;
- `GET /api/v1/shares/{shareId}/entries` lists up to 200 handle-verified entries with size and last-modified metadata;
- `POST /api/v1/transfers`, `PATCH .../content`, `GET`, `DELETE` and `POST .../complete` implement process-local, non-resumable upload state;
- upload chunks are bounded to 4 MiB, written only to private staging, flushed before the committed offset advances, SHA-256 verified, then completed with the existing no-replace handle primitive;
- transfer ownership is bound to the authenticated paired device and permissions are checked per operation;
- revoking a paired device marks its active in-memory transfers cancelled and immediately attempts to close/delete their private staging; registry revocation remains authoritative even if an OS cleanup call reports failure;
- active transfers are bounded per device and by a total staging quota;
- `GET /api/v1/shares/{shareId}/content` streams from a stable open handle with a strong SHA-256 ETag and one open-ended `Range` plus `If-Match` resume form;
- typed errors do not expose physical paths or native exception details.

`feature/android-saf-transfer` adds the Android client boundary without expanding `PairingRepository`:

- `FileTransferRepository` owns authenticated file API calls and creates a dedicated pinned-mTLS OkHttp client using the saved SPKI pin plus Android Keystore client identity;
- `RemotePathRules` mirrors the Windows wire syntax boundary before sending SAF display names or remote paths; content URIs are never converted to filesystem paths;
- `ACTION_OPEN_DOCUMENT` selects upload sources and `CREATE_DOCUMENT` selects download destinations;
- upload hashes and counts the source stream first, reopens the URI, sends 1 MiB chunks with exact `Upload-Offset`, and verifies server transfer status after ambiguous network responses before deciding whether to continue/cancel;
- source streams need not be seekable, but the provider must permit a second open because v1 requires SHA-256 and total size before transfer creation;
- download streams directly into the SAF destination, checks declared length, recomputes the strong SHA-256 ETag and reports success only when both match; a failed destination is best-effort truncated and is never reported as complete;
- long-running transfer ownership is in a non-exported `dataSync` Foreground Service rather than Activity/ViewModel; the UI observes a process-local StateFlow and can cancel the service;
- the service attempts temporary-to-persistable SAF grants for the operation and releases any grant it successfully persisted when the transfer ends;
- only one Android foreground transfer is owned at a time in this basic increment.

The current server upload registry and Android transfer-status bus are intentionally process-local. An orderly Windows shutdown disposes active sessions and deletes their uncompleted staging through the handle-safe adapter. A hard PC/Android process or OS crash can leave server staging remnants because durable startup reconciliation/cleanup is **not implemented yet**. Transfer status, committed offsets, DB/file reconciliation, crash recovery and restart resume are therefore not claimed by this increment.

Entry-list pagination cursors are also not implemented yet; the first page is capped at 200 and non-empty cursors are rejected rather than silently ignored.

## Security/audit hardening already closed

- paired-device authorization is read-only; `last_seen_at` telemetry is separate and throttled;
- Windows reports mDNS available only after DNS-SD callback success;
- Android retries identical NSD candidates after transient authentication/network failure;
- Android saved-PC persistence uses a versioned envelope, reads the legacy list format and rejects inputs above 128 KiB before JSON parsing;
- Bouncy Castle is updated from 1.83 to 1.85.2;
- Windows Forms consumes LAN adapter discovery through Host rather than directly reaching Infrastructure;
- basic file share IDs are opaque and process-scoped rather than deterministic root-path digests;
- paired-device revocation also cancels and closes that device's active process-local transfer staging;
- Android SAF paths stay as capabilities/URIs and never become OS path strings; upload and download both perform end-to-end SHA-256 validation against the Windows API contract;
- Android long-running file I/O is owned by a non-exported dataSync Foreground Service.

Operational/release hardening still outside this increment: protect `main` with required PR/CI checks, apply explicit per-user ACL policy consistently to existing application-state stores, optionally pin third-party GitHub Actions to immutable SHAs, complete real ReFS/volume-mount acceptance, and add startup cleanup/reconciliation for crash-left staging as part of durable transfer recovery.

## Remaining Phase 2–6

Remaining: physical Android-to-Windows pairing/mDNS/SAF acceptance, durable resume/recovery, restart/process-kill reconciliation, ACTION_SEND/MULTIPLE, text/history, recovery scheduler and large-file/device acceptance.

Windows does not yet automatically rebind after DHCP/Wi-Fi adapter changes; the user can currently restart the connection from the tray UI.

## Required physical-device acceptance

Windows 11: initial launch/tray exit, private-network firewall, DNS-SD advertisement, QR approval, non-exportable key, DHCP/Wi-Fi behavior, ReFS/real mounted-volume filesystem behavior, sleep/resume, multi-GB streaming, disk-full and later crash/recovery behavior.

Android: NSD on real Wi-Fi, QR camera, mTLS Keystore signature, DHCP/Wi-Fi rediscovery, `ACTION_OPEN_DOCUMENT` / `CREATE_DOCUMENT`, seekable and nonseekable/reopenable SAF providers, 4 GiB+ transfer, notification permission behavior, screen-off, dataSync foreground-service timeout and process kill. ACTION_SEND/MULTIPLE remains a later UX increment.

## Continuation order

1. Finish CI/audit for `feature/android-saf-transfer`; verify manifest/Foreground Service restrictions, URI capability handling, mTLS ownership, hash verification and cancellation without weakening the Windows boundary.
2. Run basic real-device Android↔Windows upload/download acceptance, including nonseekable/reopenable providers and failure during hashing/upload/export.
3. Implement durable resume/recovery as a separate state-machine increment: persistent transfer DB, committed-offset recovery/truncate, startup staging reconciliation/cleanup, crash between rename/DB commit, disk-full and cancellation/revocation races.
4. Add ACTION_SEND/MULTIPLE and text/history separately.
5. Run physical Windows/Android acceptance throughout; CI is not product acceptance.
