# ADR 004-http-client: OkHttp

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use OkHttp with coroutine adapters and streaming request/response bodies.

## Alternatives and rationale

Its JVM TLS configuration supports Android Keystore key managers and a per-paired-PC trust manager. Ktor is valid but adds an engine abstraction not needed for an Android-only client.

## Consequences

Cancel underlying Call on coroutine cancellation. Disable redirects for authenticated LAN calls. Configure trust per PC, never global permissive trust or hostname verification.
