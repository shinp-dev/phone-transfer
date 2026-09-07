# ADR 008-filesystem: Windows handle containment

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use NTFS/ReFS local shares only, reject reparse points, retain ancestor handles and validate final opened handle path.

## Alternatives and rationale

Path.GetFullPath containment and FileAttributes checks alone have a check/use race. ResolveLinkTarget alone does not prevent a subsequent junction replacement.

## Consequences

Do not expose file APIs until Windows-specific implementation and adversarial integration tests pass. Deny unsupported filesystem roots, UNC paths and unsafe share setups. No overwrite and same-volume staging are mandatory.
