# ADR 008-filesystem: Windows handle containment

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use NTFS/ReFS local shares only, reject reparse points, retain ancestor handles and validate final opened handle path.

Share configuration may be implemented before network file APIs, but selecting a folder does not make it remotely accessible. Configuration-time checks reject UNC paths, volume roots, non-NTFS/ReFS filesystems, reparse points in the selected root ancestry and overlap with the application's own data directory. These checks are defense in depth only and never replace handle-based containment during file operations.

## Alternatives and rationale

Path.GetFullPath containment and FileAttributes checks alone have a check/use race. ResolveLinkTarget alone does not prevent a subsequent junction replacement.

## Consequences

Do not expose file APIs until Windows-specific handle-safe implementation and adversarial integration tests pass. Deny unsupported filesystem roots, UNC paths and unsafe share setups. No overwrite and same-volume staging are mandatory.
