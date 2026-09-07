# ADR 013: Android pairing identity and trust

Date: 2026-09-08 JST
Status: Implemented; physical-device acceptance pending

The QR SPKI is the server trust anchor. System public CAs and IP SAN validation cannot
replace it: Windows uses a self-signed certificate with a stable identity DNS SAN,
while the QR routes to a private IPv4 address. `PinnedTrustManager` checks the exact
SPKI digest, validity, non-CA status, signing usage and server-auth EKU. Routing is
limited to the validated host and port. Redirects, proxies and cleartext fallback are
disabled. The class-local `CustomX509TrustManager` lint annotation documents this
intentional trust model; TrustAllX509TrustManager and other TLS lint checks remain
enabled. Unit tests reject wrong pins, expired certificates, client-only usage, CA
certificates and absent certificates.

BC's bcprov ASN.1 certificate generator encodes the TBS certificate and standard
extensions. Java Signature signs through the Android Keystore provider. No BC provider
is installed, private key export is never requested, and bcpkix's unrelated network
implementations are not packaged. The resulting certificate is parsed by the platform
CertificateFactory and verified against the Keystore public key before use.

The device UUID and public certificate use AtomicFile; failed writes fail the operation.
The private key remains in AndroidKeyStore. Existing UUID/key mismatches fail closed.
The saved-PC file contains only stable PC identity, display name, endpoint and pin.
The QR token and request proof are never persisted or logged. All files remain covered
by ADR011's backup exclusions.

A cancellation cancels active HTTP calls and prevents persistence when observed before
the atomic save. It cannot reverse Windows approval already performed. Local removal
also cannot revoke the Windows allowlist; the UI directs the user to revoke on Windows
before re-pairing. Saved-PC persistence occurs only after approved status and matching
mTLS info. mDNS rediscovery is a separate pending increment.
