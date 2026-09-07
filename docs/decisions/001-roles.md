# ADR 001-roles: PC server / Android client

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use one Windows Kestrel listener and Android initiated requests.

## Alternatives and rationale

Bidirectional listeners complicate Android background lifetime, discovery, firewall and pairing. PC-to-phone push can later use an Android-originated WebSocket.

## Consequences

No Android inbound server; Windows Service is a future host adapter.
