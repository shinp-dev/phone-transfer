# Implementation status

Updated: 2026-09-08

See [development handoff](handoff.md) for the current continuation order. This repository is still pre-MVP: authentication/discovery/share/filesystem foundations are implemented, but file/text transfer routes are not yet exposed.

## Phase 1 — complete

Implemented: monorepo, layer boundaries, tray/Compose startup shells, OpenAPI wire schemas and deterministic DTO generation, CI definitions, domain path syntax/state/offset/permission rules, stream digest verifier, stable device identity adapter, restricted Kestrel host factory and `/api/v1/info`, unit and real-TLS tests, ADRs and threat model.

All Phase 1 gates passed at `38983e90ea191faabacdcd12ab85f2d44f48127e`: Android build/unit tests/lint/Spotless, Windows build/46 tests/format, protocol validation/generated model checks and diff whitespace checks. [CI evidence](https://github.com/shinp-dev/phone-transfer/actions/runs/34145840930). Android upgrade notices remain informational as documented in ADR 011.

## Phase 2 — pairing and mDNS implemented; physical acceptance pending

Implemented as application components: a single-active QR challenge store (256-bit randomness, monotonic 120-second expiry, single use, atomic callback, replacement/invalidation and redacted ToString), and bounded ECDSA P-256 certificate proof verification. Verification binds the display name, device UUID, QR token and certificate fingerprint. It rejects invalid validity/usage/curve/encoding and does not fetch certificate-chain resources.

The Windows backend includes the local approval coordinator, signature-authorized status polling, isolated HTTPS pairing host with body/concurrency/rate limits, SQLite device registration and revocation, current-user non-exportable CNG certificate adapter, tray/runtime composition and bounded shutdown. Android includes QR validation, offline scanning, Keystore identity, pinned HTTPS registration, signed polling, comparison-code UI, mTLS info verification before atomic persistence, connection checks and local removal.

The Android saved-PC store is application-singleton owned and updates its last-known endpoint atomically. Production mDNS is wired: Windows advertises `_phone-transfer._tcp` through the Windows DNS-SD API on the selected private IPv4 interface with only `version` and stable `deviceId` TXT values; Android uses `NsdManager` with a multicast lock and accepts only private IPv4 API endpoints on port 58443. A changed endpoint is persisted only after the stored SPKI pin, client certificate and `/api/v1/info` stable device ID all verify. Discovery remains untrusted and QR remains the trust bootstrap/fallback.

## Phase 3 — handle-safe filesystem foundation implemented; transfer APIs disabled

Windows can select, persist and clear one receive-folder root without exposing it through the network API. Configuration accepts only existing local NTFS/ReFS folders, rejects UNC paths, volume roots, overlap with the application's own data directory and reparse points in the selected path ancestry, and writes updates through a same-directory temporary file before replace/move. This is a configuration-time guard and remains independent from runtime containment.

PR #7 added the standalone Windows handle-safe adapter: pinned root/traversal, bounded handle-based listing, stable file reads, private ACL-protected staging and atomic same-volume no-overwrite completion. It opens child components relative to retained directory handles and rejects reparse points/junctions/symlinks/hardlinks and replacement races fail-closed. See ADR 008 for containment invariants and ownership rules.

Final PR #7 HEAD `f8bfd5ded9da2fb95bf1518feb9bdf1623589487` passed Windows format, Release build and **109 tests with 0 skipped**; protocol and Android regression jobs also passed. [CI evidence](https://github.com/shinp-dev/phone-transfer/actions/runs/34187016589). PR #7 is merged to main as `d0bc325bbf84b5e697557c3410b78bbf98fb1466`.

No `/api/v1/shares`, entry listing, upload, download, transfer-state or text route is enabled yet. Quotas, authorization orchestration, durable transfer records and resume/recovery remain separate work.

## Post-PR5 audit hardening

The whole-repository review after PR #5 found no critical/high issue requiring rollback. The low-risk findings closed before file-transfer work are:

- paired-device authorization is read-only; `last_seen_at` telemetry is separate and throttled;
- Windows reports mDNS available only after DNS-SD callback success;
- Android allows retry of identical NSD candidates after transient authentication/network failure;
- Android saved-PC persistence writes a versioned envelope while continuing to read the legacy list format;
- Windows Forms consumes LAN adapter discovery through Host rather than directly reaching Infrastructure.

The pre-transfer cleanup additionally updates Bouncy Castle from 1.83 to 1.85.2 and caps Android saved-PC persistence input at 128 KiB before JSON parsing.

Operational/release hardening still outside this increment: protect `main` with required PR/CI checks, apply explicit per-user ACL policy consistently to existing application-state stores, and optionally pin third-party GitHub Actions to immutable commit SHAs.

## Remaining Phase 2–6

Remaining: physical Android-to-Windows pairing/mDNS acceptance, basic list/upload/download routes, Android SAF, durable resume/recovery, foreground/background transfer lifetime, text/history UI and recovery scheduler.

The tray starts the isolated bootstrap listener and the mTLS info listener on one selected private IPv4 LAN adapter and attempts DNS-SD advertisement without making it a prerequisite for authenticated listeners. Windows does not yet automatically rebind after DHCP/Wi-Fi adapter changes; the user can currently restart the connection from the tray UI.

## Required physical-device acceptance

Windows 11: initial launch/tray exit, private-network firewall, DNS-SD advertisement, QR approval, non-exportable key, DHCP/Wi-Fi change behavior, ReFS/real mounted-volume filesystem behavior, sleep/resume, and later revocation/disk-full/crash behavior during streaming.

Android: NSD on real Wi-Fi, QR camera, mTLS Keystore signature, DHCP/Wi-Fi rediscovery, ACTION_SEND/MULTIPLE content URIs, SAF providers (seekable and nonseekable), 4 GiB+ transfer, notification permission, screen-off, process kill and foreground-service timeout.

## Continuation order

1. Merge the small pre-transfer cleanup after CI.
2. Add basic authenticated share/list/upload/download endpoints using only the handle-safe filesystem adapter; keep resume/recovery out of this first transfer PR.
3. Add Android SAF and transfer repository/service without expanding `PairingRepository` into transfer ownership.
4. Implement durable resume/recovery, background lifetime and text/history in separate increments.
5. Run physical Windows/Android acceptance throughout; do not treat CI alone as product acceptance.
