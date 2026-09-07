# ADR 012: Local approval, proof-authorized polling and durable device admission

Date: 2026-09-07
Status: Accepted; host adapters and integration tests implemented, production UI composition pending.

## Decision

Compose challenge consumption and proof verification in PairingCoordinator. It holds at most one pending request in memory. Submission does not authorize the phone. A local approval writes the registered public certificate and identity to SQLite before reporting approval. Approval and denial are serialized; expired, closed or replaced sessions cannot be approved. An existing active Device ID cannot be overwritten. Re-pairing after explicit revocation may replace its certificate, and the old fingerprint remains unauthorized.

Polling carries a signature with a separate protocol domain separator and request UUID. It authenticates the pending phone key, not just knowledge of a request ID. Replaying this proof reveals only status and cannot mutate authority. Status proofs are never logged. A future protocol requiring confidential status across delegated observers can add nonce/timestamp freshness without granting mutation rights to this read endpoint.

Use a separate Kestrel HTTPS bootstrap host with bounded metadata, global concurrency and operation-rate limits. No file, info, approval or revocation routes are served on that listener. Typed JSON errors cover malformed bodies, unknown properties, missing proof, invalid/expired challenges and rate limits. The transfer host continues to require mTLS and rechecks SQLite admission on every request, including pooled connections.

WindowsServerCertificate creates a named, current-user CNG P-256 signing key with export disabled and stores its certificate in CurrentUser/My. A stable Device ID determines its key name; PC display-name changes do not affect it. Certificates renew using the same key when less than 30 days remain, preserving SPKI pinning. Key loss/rotation changes the pin and requires explicit re-pairing. Test-created certificates and CNG keys are removed using the unique test Device ID.

SQLite uses schema version 1, parameterized statements, WAL and FULL synchronization. Unsupported future schema versions fail closed. Authorization checks certificate expiry and persisted revocation each time. Devices, certificates, permissions and registration/last-seen timestamps are durable; QR secrets and pending sessions are not stored. Registry failures propagate rather than silently authorizing a device.

## Alternatives

Automatic approval after a valid QR proof would let an observer register their own key. Persisting pending secrets would create recovery and secrecy obligations for short-lived state. Putting pairing on the mTLS listener would complicate the unpaired-client boundary. A generic repository or ORM is unnecessary for this small, explicit device table.

## Limits

The tray does not yet compose these hosts or show QR/local approval. Android Keystore/QR/pinned transport and mDNS remain to be integrated. Streaming revocation and file-transfer ownership are future transfer-phase tests; the current integration test proves revocation for an existing HTTP connection and a new handshake. This is not a completed end-user pairing flow.
