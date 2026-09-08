# Architecture

Windows is the only listener. Android initiates discovery, pairing and API requests. No cloud account, relay, analytics or external API is part of the runtime design. Package downloads are build-time only.

## Boundaries

- `PhoneTransfer.App`: Windows Forms tray and local user approval; owns host lifetime. No networking or filesystem logic in UI.
- `PhoneTransfer.Host`: composition root and Kestrel lifetime; reusable from a future service host.
- `PhoneTransfer.Api`: HTTP mapping, typed validation/error translation. No direct filesystem operations.
- `PhoneTransfer.Application`: orchestration, authorization and cancellation contracts.
- `PhoneTransfer.Domain`: paths, transfer transitions, offset arithmetic and permission rules; no platform dependencies.
- `PhoneTransfer.Infrastructure/{Security,Discovery,Persistence,Storage}`: OS adapters. Separate folders first; separate assemblies only if necessary.
- Android: Compose → ViewModel/StateFlow → repository → network/security/storage/discovery adapters. A user-started transfer service owns ongoing work; Activities do not own transfer lifetime.
- `packages/protocol/openapi.json`: wire-model SSOT; deterministic C#/Kotlin models in `Generated` / protocol module. Generated DTOs are not domain objects and do not enforce request validation by themselves.

## Concurrency and recovery design

A per-transfer mutation gate serializes append, verify, complete, cancel and cleanup without holding one global transfer lock across long file I/O. Durable metadata is the authority; acknowledged offsets follow flushed file bytes and committed metadata. Startup truncates bytes beyond the durable offset. Missing/short files become failed, never silently resumed. Completion uses a same-volume rename without overwrite; reconciliation proves the expected destination by persisted file identity, size and digest if a crash occurs between rename and database commit.

Revocation removes authorization before cancelling active operations. Each API request and each chunk commit checks current authorization. Cached TLS handshakes alone cannot grant application permission. A request already committed before revocation cannot be undone.

Settings, paired devices, transfer records and text history are distinct models. UI speed and instantaneous progress remain runtime state. Stable device UUID is independent of machine name and addresses.

## Implementation status

See [implementation-status](../implementation-status.md). This document describes target architecture; it does not imply that all adapters exist.
