# ADR 006-resume: Server committed offsets

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use sequential bounded chunks and durable offsets; per-transfer lock and idempotency keys.

## Alternatives and rationale

Parallel chunk maps increase memory, reconciliation and ordering complexity for a single-LAN app. Standard HTTP PATCH/Range is sufficient; no custom RPC.

## Consequences

File flush precedes metadata commit. Startup truncates uncommitted tails. Final whole-file hash and same-volume no-overwrite rename; reconcile crash windows. Pause and cancellation are distinct.
