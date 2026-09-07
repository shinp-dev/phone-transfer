# ADR 007-persistence: SQLite plus protected OS identity

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use SQLite transactions for device records, transfer metadata and optional history; OS stores for private keys.

## Alternatives and rationale

JSON is sufficient for immutable initial device ID but concurrent transfers/history and crash recovery need transactional records. Generic repositories and a server database are unnecessary.

## Consequences

Schema migrations are versioned and backed up; never wipe state on unknown version or corruption. Settings and runtime progress are separate. Retention cleanup uses transfer ownership and locks.
