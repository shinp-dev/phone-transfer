# ADR 002-discovery: mDNS discovery

Date: 2026-09-07
Status: Accepted and implemented; physical-network acceptance remains.

## Decision

Use Android `NsdManager` and Windows DNS-SD advertisement of `_phone-transfer._tcp`.

The Windows API listener advertises port `58443` on the explicitly selected private IPv4 interface. The service instance TXT record contains exactly two identity hints:

- `version=1`
- `deviceId=<canonical UUID>`

The Android client keeps NSD active while its foreground UI is alive and holds a Wi-Fi multicast lock for compatibility with older Android releases. A discovered address is accepted only when it is a numeric private IPv4 endpoint on port `58443`.

Discovery is never an authentication source. For an already paired PC, Android first connects to the discovered candidate with the stored SPKI pin and client certificate, verifies `/api/v1/info` returns the same stable device ID and protocol version, and only then replaces the persisted last-known endpoint. An unpaired discovery result does not bootstrap trust or bypass the QR flow.

## Alternatives and rationale

UDP broadcast is simple but requires a custom packet format, retransmission policy and more firewall behavior. mDNS supplies standard service records and native Android integration. Windows uses the operating-system DNS-SD API rather than a custom responder or an additional networking library.

## Consequences

DHCP address changes can be recovered without treating an IP address as device identity. Multicast-isolated networks can still use the QR/manual endpoint fallback. DNS-SD registration failure does not stop the authenticated HTTPS listeners. Real Wi-Fi discovery, multicast-isolated networks and DHCP changes remain physical-device acceptance items.
