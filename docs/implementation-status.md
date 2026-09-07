# Implementation status

Updated: 2026-09-07

## Phase 1 — complete

Implemented: monorepo, layer boundaries, tray/Compose startup shells, OpenAPI wire schemas and deterministic DTO generation, CI definitions, domain path syntax/state/offset/permission rules, stream digest verifier, stable device identity adapter, restricted Kestrel host factory and `/api/v1/info`, unit and real-TLS tests, ADRs and threat model.

All Phase 1 gates passed at `38983e90ea191faabacdcd12ab85f2d44f48127e`: Android build/unit tests/lint/Spotless, Windows build/46 tests/format, protocol validation/generated model checks and diff whitespace checks. [CI evidence](https://github.com/shinp-dev/phone-transfer/actions/runs/34145840930). Android upgrade notices remain informational as documented in ADR 011.

## Phase 2 — Windows admission adapters in progress

Implemented as application components: a single-active QR challenge store (256-bit randomness, monotonic 120-second expiry, single use, atomic callback, replacement/invalidation and redacted ToString), and bounded ECDSA P-256 certificate proof verification. Verification binds the display name, device UUID, QR token and certificate fingerprint. It rejects invalid validity/usage/curve/encoding and does not fetch certificate-chain resources.

The cryptographic primitives passed fifteen added test cases. New in this increment: local approval coordinator, signature-authorized status polling, isolated HTTPS pairing host with body/concurrency/rate limits, SQLite device registration and revocation, current-user non-exportable CNG certificate adapter. New integration tests cover real HTTPS submission → local approval → mTLS info → pooled-connection revocation, persistent state, policy expiry and malformed/oversized/rate-limited input. These backend changes passed Windows, Android and protocol CI at `507862cd10767922aca57b8cff1d0a46ad742ac0`: [CI evidence](https://github.com/shinp-dev/phone-transfer/actions/runs/34146429559).

The next increment composes the production Windows runtime into the tray: LAN adapter selection, QR rendering, explicit comparison-code approval, durable device listing/revocation, and bounded host shutdown. Closing the QR denies unapproved requests while preserving a completed status receipt until its original expiry. Added tests exercise that lifecycle and production CNG-backed TLS across a host restart. CI validation of this UI/runtime increment is pending.

## Remaining Phase 2–6

Remaining: production mDNS, Android QR scanner, Android Keystore/pinned transport and saved pairing, share configuration, Windows handle-safe filesystem, transfer endpoints/records, upload/download/resume orchestration, Android SAF/foreground service/share intents, text/history UI and recovery scheduler. Host factories implement `/api/v1/info` and the two `/pairing/v1/requests` routes; remaining OpenAPI routes are design contracts.

The tray now starts the isolated bootstrap listener and the mTLS info listener on one selected private IPv4 LAN adapter. No file or text transfer route is implemented. Discovery currently enumerates local adapters only; remote mDNS discovery remains unimplemented.

## Required physical-device acceptance

Windows 11: initial launch/tray exit, private-network firewall, QR approval, non-exportable key, share junction replacement, revocation during streaming, disk-full/crash recovery, sleep/resume.

Android: NSD on real Wi-Fi, QR camera, mTLS Keystore signature, ACTION_SEND/MULTIPLE content URIs, SAF providers (seekable and nonseekable), 4 GiB+ transfer, notification permission, screen-off, process kill, foreground-service timeout and Wi-Fi/DHCP change.

## Continuation order

1. Complete the Windows tray/runtime integration-test gates.
2. Verify Windows tray interaction on a physical machine.
3. Add Android Keystore/pinned HTTPS/QR and mDNS, then verify real Android-to-Windows pairing.
4. Continue Phases 3–6 in the original order.
