# Implementation status

Updated: 2026-09-07

## Phase 1 — in progress

Implemented: monorepo, layer boundaries, tray/Compose startup shells, OpenAPI wire schemas and deterministic DTO generation, CI definitions, domain path syntax/state/offset/permission rules, stream digest verifier, stable device identity adapter, restricted Kestrel host factory and `/api/v1/info`, unit and real-TLS tests, nine ADRs and threat model.

Validation is in progress. No phase has been declared complete until build, test, lint/format and protocol checks have passed.

## Phase 2 — cryptographic primitives in progress

Implemented as application components, not exposed endpoints: a single-active QR challenge store (256-bit randomness, monotonic 120-second expiry, single use, atomic callback, replacement/invalidation and redacted ToString), and bounded ECDSA P-256 certificate proof verification. Verification binds the display name, device UUID, QR token and certificate fingerprint. It rejects invalid validity/usage/curve/encoding and does not fetch certificate-chain resources.

Fifteen added test cases cover token replay/expiry/concurrency/failure and proof substitution/malformed metadata. Validation is in progress; see the latest PR checks.

## Remaining Phase 2–6

No production discovery, QR renderer/scanner, complete pairing/approval service, OS-backed certificate lifecycle, persisted allowlist/revocation UI, share configuration, Windows handle-safe filesystem, transfer endpoints, SQLite records, upload/download/resume orchestration, Android SAF/foreground service/share intents, text/history UI or recovery scheduler exists yet. OpenAPI routes other than `/api/v1/info` are design contracts, not callable features.

The development tray deliberately does not start a server. Do not expose the host factory to LAN until the production authorization adapter and certificate lifecycle are implemented. The callback is injected for integration testing, not a built-in trust policy.

## Required physical-device acceptance

Windows 11: initial launch/tray exit, private-network firewall, QR approval, non-exportable key, share junction replacement, revocation during streaming, disk-full/crash recovery, sleep/resume.

Android: NSD on real Wi-Fi, QR camera, mTLS Keystore signature, ACTION_SEND/MULTIPLE content URIs, SAF providers (seekable and nonseekable), 4 GiB+ transfer, notification permission, screen-off, process kill, foreground-service timeout and Wi-Fi/DHCP change.

## Continuation order

1. Finish all Phase 1 CI gates; do not confuse passing primitive tests with a working pairing flow.
2. Compose OS-backed key/certificate adapters and durable device allowlist, then local approval and proof-authorized polling with bounded pending sessions.
3. Add isolated pinned-HTTPS bootstrap listener, mDNS and QR UI, then verify real Android Keystore mTLS to Windows.
4. Continue Phases 3–6 in the original order. No live transfer listener is enabled by the primitives added here.
