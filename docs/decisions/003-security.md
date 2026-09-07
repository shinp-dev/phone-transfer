# ADR 003-security: mTLS with explicit QR bootstrap

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Separate pinned HTTPS pairing from mandatory-mTLS transfer listener.

## Alternatives and rationale

An unpaired device has no registered identity yet. Accepting arbitrary certificates on transfer APIs creates an accidental unauthenticated surface. QR transfers a server pin and short-lived token; local approval binds the phone key.

## Consequences

Windows CNG and Android Keystore non-exportable keys. Check validity, usages, pin and current allowlist. Default certificate replacement is explicit re-pairing. Same-key server renewal retains SPKI pin; key rotation requires authenticated overlap or re-pair, never TOFU fallback.
