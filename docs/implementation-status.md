# Implementation status

Updated: 2026-09-08

See [development handoff](handoff.md) for the checkpoint scope and exact continuation order. This checkpoint may be merged to main before the MVP is complete.

## Phase 1 — complete

Implemented: monorepo, layer boundaries, tray/Compose startup shells, OpenAPI wire schemas and deterministic DTO generation, CI definitions, domain path syntax/state/offset/permission rules, stream digest verifier, stable device identity adapter, restricted Kestrel host factory and `/api/v1/info`, unit and real-TLS tests, ADRs and threat model.

All Phase 1 gates passed at `38983e90ea191faabacdcd12ab85f2d44f48127e`: Android build/unit tests/lint/Spotless, Windows build/46 tests/format, protocol validation/generated model checks and diff whitespace checks. [CI evidence](https://github.com/shinp-dev/phone-transfer/actions/runs/34145840930). Android upgrade notices remain informational as documented in ADR 011.

## Phase 2 — pairing and mDNS implemented; physical acceptance pending

Implemented as application components: a single-active QR challenge store (256-bit randomness, monotonic 120-second expiry, single use, atomic callback, replacement/invalidation and redacted ToString), and bounded ECDSA P-256 certificate proof verification. Verification binds the display name, device UUID, QR token and certificate fingerprint. It rejects invalid validity/usage/curve/encoding and does not fetch certificate-chain resources.

The cryptographic primitives passed fifteen added test cases. The Windows backend includes the local approval coordinator, signature-authorized status polling, isolated HTTPS pairing host with body/concurrency/rate limits, SQLite device registration and revocation, current-user non-exportable CNG certificate adapter, tray/runtime composition and bounded shutdown. Android includes QR validation, offline scanning, Keystore identity, pinned HTTPS registration, signed polling, comparison-code UI, mTLS info verification before atomic persistence, connection checks and local removal.

The Android saved-PC store is application-singleton owned and updates its last-known endpoint atomically. Production mDNS is now wired: Windows advertises `_phone-transfer._tcp` through the Windows DNS-SD API on the selected private IPv4 interface with only `version` and stable `deviceId` TXT values; Android uses `NsdManager` with a multicast lock and accepts only private IPv4 API endpoints on port 58443. A changed endpoint is persisted only after the stored SPKI pin, client certificate and `/api/v1/info` stable device ID all verify. Discovery remains untrusted and QR remains the trust bootstrap/fallback.

## Phase 3 — internal handle-safe filesystem adapter; transfer APIs disabled

Windows can select, persist and clear one receive-folder root without exposing it through the network API. The setting is versioned and stored separately from paired-device state. Configuration accepts only existing local NTFS/ReFS folders, rejects UNC paths, volume roots, overlap with the application's own data directory and reparse points in the selected path ancestry, and writes updates through a same-directory temporary file before replace/move. This is a configuration-time guard only; it is not a substitute for the handle-safe file adapter required by ADR 008.

The standalone Windows adapter now provides pinned root/traversal, bounded handle-based listing, stable file reads, private ACL-protected staging and atomic no-overwrite completion. See ADR 008 for containment invariants, ownership, adversarial tests and filesystem coverage limits. Windows CI at `461b50df6da429831c6d5176efbad0790a351faa` passed format, Release build and 107 tests without skips; subsequent revisions must pass the same gates. See [PR #7](https://github.com/shinp-dev/phone-transfer/pull/7) for final CI evidence. No list/upload/download route is enabled; quotas, authorization orchestration, durable records and resume/recovery remain separate work.

## Post-PR5 audit hardening

The first whole-repository review after PR #5 found no critical/high issue that required rolling back the merged checkpoint. The low-risk findings that can be closed before file-transfer work are implemented in the audit hardening branch:

- paired-device authorization is a read-only SQLite lookup; `last_seen_at` is updated separately, at most once per minute per observed row, and failure to update that telemetry does not authorize a revoked device;
- the Windows runtime reports mDNS as available only after the asynchronous Windows DNS-SD registration callback confirms success;
- Android does not suppress an identical NSD candidate before it has been authenticated, allowing retry after transient TLS/network failure;
- Android saved-PC persistence now writes a versioned envelope while continuing to read the pre-versioning list format and retaining the serialized `endpoint` field for compatibility;
- Windows Forms code consumes LAN adapter discovery through the Host boundary rather than directly referencing Infrastructure discovery types.

Operational/release hardening still outside this code increment: protect `main` with required PR/CI checks, apply the explicit per-user ACL policy consistently to existing application-state stores (new staging already has an atomic protected current-user DACL), and optionally pin third-party GitHub Actions to immutable commit SHAs.

## Remaining Phase 2–6

Remaining: physical Android-to-Windows pairing and mDNS acceptance, transfer endpoints/records, upload/download/resume orchestration, Android SAF/foreground service/share intents, text/history UI and recovery scheduler. Host factories implement `/api/v1/info` and the two `/pairing/v1/requests` routes; remaining OpenAPI routes are design contracts.

The tray starts the isolated bootstrap listener and the mTLS info listener on one selected private IPv4 LAN adapter and attempts DNS-SD advertisement without making it a prerequisite for the authenticated listeners. No file or text transfer route is implemented.

## Required physical-device acceptance

Windows 11: initial launch/tray exit, private-network firewall, DNS-SD advertisement, QR approval, non-exportable key, share junction replacement, revocation during streaming, disk-full/crash recovery, sleep/resume.

Android: NSD on real Wi-Fi, QR camera, mTLS Keystore signature, DHCP/Wi-Fi change rediscovery, ACTION_SEND/MULTIPLE content URIs, SAF providers (seekable and nonseekable), 4 GiB+ transfer, notification permission, screen-off, process kill and foreground-service timeout.

## Continuation order

1. Verify Windows tray, QR pairing and mDNS on physical Windows/Android devices, including a DHCP address change.
2. Accept the standalone Windows handle-safe adapter after Windows CI and review of ADR 008; keep network file routes disabled in this increment.
3. Add basic list/upload/download endpoints and Android SAF integration.
4. Continue durable resume/recovery, background transfer and text/history in separate increments.
