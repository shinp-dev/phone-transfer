# ADR 009-android-lifetime: User initiated transfers

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use a user-started dataSync foreground service, notifications and durable transfer records.

## Alternatives and rationale

Activity scope dies on navigation. Android background restrictions prohibit treating foreground services as permanent daemons. A 6-hour dataSync budget applies on Android 15+ under documented conditions.

## Consequences

Handle service timeout by persisting paused state and stopping promptly. Do not restart from BOOT_COMPLETED. Persist SAF grants where offered; ephemeral shared content must be staged while readable. Re-discovery is tied to network/lifecycle; no permission bypass.
