# Implementation status

Updated: 2026-09-08

See [development handoff](handoff.md) for continuation order. The repository is still pre-MVP: pairing, mDNS, shared-folder configuration, Windows handle-safe filesystem and the first authenticated Windows file-transfer API are implemented. Android SAF and durable resume/recovery are not yet implemented.

## Phase 1 — complete

Implemented: monorepo, layer boundaries, tray/Compose startup shells, OpenAPI wire schemas and deterministic DTO generation, CI definitions, domain path syntax/state/offset/permission rules, stream digest verifier, stable device identity adapter, restricted Kestrel host factory and `/api/v1/info`, unit and real-TLS tests, ADRs and threat model.

## Phase 2 — pairing and mDNS implemented; physical acceptance pending

The Windows backend includes single-use 120-second QR challenges, ECDSA P-256 proof verification, local comparison-code approval, signature-authorized status polling, isolated HTTPS pairing, SQLite device registration/revocation, current-user non-exportable CNG server identity, request-by-request revocation checks and bounded shutdown.

Android includes QR validation, offline scanning, Keystore identity, pinned HTTPS registration, signed polling, comparison-code UI, mTLS `/api/v1/info` verification before atomic persistence, saved-PC endpoint persistence and local removal.

Production mDNS is wired: Windows advertises `_phone-transfer._tcp` through Windows DNS-SD on the selected private IPv4 interface; Android uses `NsdManager` with a multicast lock. Discovery is routing metadata only. A changed endpoint is saved only after the stored SPKI pin, client certificate and stable `/api/v1/info` Device ID all verify.

## Phase 3 — Windows handle-safe filesystem and basic file API implemented

Windows can select, persist and clear one receive-folder root. Configuration accepts only existing local NTFS/ReFS folders, rejects UNC paths, volume roots, overlap with application data and reparse points in the configured ancestry.

PR #7 added the standalone handle-safe filesystem adapter: pinned root/traversal, bounded handle-based listing, stable file reads, private ACL-protected staging and atomic same-volume no-overwrite completion. Child components are opened relative to retained directory handles and reparse points/junctions/symlinks/hardlinks/replacement races fail closed. PR #7 final Windows CI passed 109 tests with 0 skipped.

`feature/basic-file-transfer` adds the first authenticated network use of that adapter:

- `GET /api/v1/shares` exposes one logical share without returning the physical Windows root path;
- `GET /api/v1/shares/{shareId}/entries` lists up to 200 handle-verified entries with size and last-modified metadata;
- `POST /api/v1/transfers`, `PATCH .../content`, `GET`, `DELETE` and `POST .../complete` implement process-local, non-resumable upload state;
- upload chunks are bounded to 4 MiB, written only to private staging, flushed before the committed offset advances, SHA-256 verified, then completed with the existing no-replace handle primitive;
- transfer ownership is bound to the authenticated paired device and permissions are checked per operation;
- active transfers are bounded per device and by a total staging quota;
- `GET /api/v1/shares/{shareId}/content` streams from a stable open handle with a strong SHA-256 ETag and one open-ended `Range` plus `If-Match` resume form;
- typed errors do not expose physical paths or native exception details.

The current upload registry is intentionally process-local. A PC restart loses transfer status and abandons staging during process teardown/startup cleanup; durable committed offsets, DB/file reconciliation, crash recovery and restart resume are **not** claimed by this increment. The API reads at most one request chunk into a bounded 4 MiB buffer before the handle-safe write/flush commit, avoiding whole-file buffering and avoiding a partially committed request body without introducing truncate/recovery semantics yet.

Entry-list pagination cursors are also not implemented yet; the first page is capped at 200 and non-empty cursors are rejected rather than silently ignored.

## Security/audit hardening already closed

- paired-device authorization is read-only; `last_seen_at` telemetry is separate and throttled;
- Windows reports mDNS available only after DNS-SD callback success;
- Android retries identical NSD candidates after transient authentication/network failure;
- Android saved-PC persistence uses a versioned envelope, reads the legacy list format and rejects inputs above 128 KiB before JSON parsing;
- Bouncy Castle is updated from 1.83 to 1.85.2;
- Windows Forms consumes LAN adapter discovery through Host rather than directly reaching Infrastructure.

Operational/release hardening still outside this increment: protect `main` with required PR/CI checks, apply explicit per-user ACL policy consistently to existing application-state stores, optionally pin third-party GitHub Actions to immutable SHAs, and complete real ReFS/volume-mount acceptance.

## Remaining Phase 2–6

Remaining: physical Android-to-Windows pairing/mDNS acceptance, Android SAF file selection/export and transfer UI/service ownership, durable resume/recovery, foreground/background transfer lifetime, text/history, recovery scheduler and large-file/device acceptance.

Windows does not yet automatically rebind after DHCP/Wi-Fi adapter changes; the user can currently restart the connection from the tray UI.

## Required physical-device acceptance

Windows 11: initial launch/tray exit, private-network firewall, DNS-SD advertisement, QR approval, non-exportable key, DHCP/Wi-Fi behavior, ReFS/real mounted-volume filesystem behavior, sleep/resume, multi-GB streaming, disk-full and later crash/recovery behavior.

Android: NSD on real Wi-Fi, QR camera, mTLS Keystore signature, DHCP/Wi-Fi rediscovery, ACTION_SEND/MULTIPLE content URIs, SAF providers (seekable and nonseekable), 4 GiB+ transfer, notification permission, screen-off, process kill and foreground-service timeout.

## Continuation order

1. Finish CI/audit for the basic Windows file API and merge only after the handle-safe boundary and authorization paths pass review.
2. Add Android transfer repository/service plus SAF without expanding `PairingRepository` into transfer ownership.
3. Implement durable resume/recovery as a separate state-machine increment: persistent transfer DB, committed-offset recovery/truncate, startup reconciliation, crash between rename/DB commit, disk-full and cancellation/revocation races.
4. Add foreground/background lifetime and text/history separately.
5. Run physical Windows/Android acceptance throughout; CI is not product acceptance.
