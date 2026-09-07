# ADR 010: Bounded QR challenges and signed approval identity

Date: 2026-09-07
Status: Accepted; cryptographic primitives implemented, production pairing flow pending.

## Decision

Use one active 256-bit QR challenge per Windows process. Keep only its SHA-256 digest in the challenge store. Issuing another challenge invalidates the previous one, and closing pairing explicitly invalidates it. Expiry uses TimeProvider's monotonic timestamps; UTC expiry is display metadata. Consumption and pending-request creation share a lock. A failed creation consumes the token and requires a new QR rather than permitting replay after a partially executed callback.

Validate a proof before attempting consumption. The bootstrap signature binds the protocol domain separator, canonical device UUID, display name, QR token and certificate digest. The display name is included because it is the identity shown during local approval. This updates the original, still-unreleased v1 design; no released client exists to migrate.

Use ECDSA P-256, SHA-256 and DER-encoded ECDSA signatures. Require an explicit client-auth EKU, digital-signature usage and non-CA self-signed certificate. Reject malformed, oversized, noncanonical or expired certificates/requests. Certificate chain checks disable downloads: pairing must not cause AIA/CRL calls outside the LAN.

## Alternatives

A long-lived bearer token would simplify bootstrap but turn a photographed QR into reusable authorization. UTC-only expiry could be extended by clock correction. An unsigned display name would leave the local approval identity outside proof binding. Supporting several signing algorithms in the initial protocol increases interoperability and validation complexity without an MVP requirement.

## Boundaries

Successful proof validation is not pairing approval and does not add a device to an allowlist. The OS-backed Android key adapter, Windows persistence, pending-request timeout, proof-authorized status polling, approval UI and bootstrap listener must be composed and tested before exposing production pairing. Neither the challenge nor the request (which carries its token) may be logged or serialized into history. The callback contract is trusted in-process application code, not caller-supplied behavior.

## Validation

Tests cover expiry boundaries, wall-clock rollback, replacement/invalidation, bad token inputs, concurrent consumption, callback failure, field substitution, wrong keys, wrong usage, CA certificates, expiry/not-yet-valid, wrong curve, malformed encodings, excessive sizes and null JSON fields.
