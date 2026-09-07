# ADR 002-discovery: mDNS discovery

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use Android NsdManager and Windows DNS-SD advertisement of _phone-transfer._tcp.

## Alternatives and rationale

UDP broadcast is simple but requires a custom packet format, retransmission policy and more firewall behavior. mDNS supplies standard service records and native Android integration.

## Consequences

TXT contains only protocol version and stable device ID. Re-resolve after network changes; QR/manual endpoint fallback for multicast-isolated networks. Discovery is not trust.
