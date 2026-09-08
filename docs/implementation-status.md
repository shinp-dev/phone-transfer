# Implementation status

Updated: 2026-09-09

Current main: `0591396f40097d834ba583a10f2dd4e5953b6a3e` (PR #12 merged)

Phone Transfer is still pre-MVP because full physical-device acceptance is not complete. The core file-transfer path is now substantially implemented: pairing, mDNS discovery, shared-folder configuration, Windows handle-safe filesystem, authenticated file-transfer API, Android SAF/Foreground Service transfer ownership, Windows durable upload recovery, and Android process-kill upload recovery are all present on `main`.

See [development handoff](handoff.md) for the next work order.

## Phase 1 — complete

Implemented:

- monorepo and layer boundaries;
- Windows tray / Android Compose shells;
- OpenAPI wire schemas and deterministic generated DTOs;
- CI definitions;
- domain path/state/offset/permission rules;
- stable device identity and restricted Kestrel host;
- `/api/v1/info`;
- unit, protocol and real-TLS tests;
- ADRs and threat model.

## Phase 2 — pairing and discovery implemented; physical acceptance pending

Windows includes:

- single-use 120-second QR challenges;
- ECDSA P-256 proof verification;
- local comparison-code approval;
- signature-authorized polling;
- isolated HTTPS pairing;
- SQLite device registration/revocation;
- current-user non-exportable CNG server identity;
- request-by-request revocation checks;
- bounded shutdown.

Android includes:

- QR validation and offline scanning;
- Android Keystore identity;
- pinned HTTPS registration;
- signed polling and comparison-code UI;
- mTLS `/api/v1/info` verification before saved-PC persistence;
- saved endpoint/pin/device identity persistence and local removal.

Production mDNS is wired. Windows advertises `_phone-transfer._tcp` on the selected private IPv4 interface and Android uses `NsdManager` with a multicast lock. Discovery is routing metadata only: endpoint changes are persisted only after the stored SPKI pin, client identity and stable Device ID all verify.

## Phase 3 — authenticated handle-safe file transfer implemented

Windows can select, persist and clear one receive-folder root. Configuration accepts only existing local NTFS/ReFS folders and rejects UNC paths, volume roots, overlap with application data and reparse points in the configured ancestry.

The handle-safe filesystem adapter pins the configured root and traverses child components relative to retained handles. Reparse points, junctions, symlinks, hardlinks, identity replacement races and unsafe mount/path transitions fail closed. Staging is private and completion uses same-volume atomic no-overwrite rename.

The authenticated file API provides:

- `GET /api/v1/shares`;
- `GET /api/v1/shares/{shareId}/entries`;
- `POST /api/v1/transfers`;
- `GET /api/v1/transfers/{transferId}`;
- `PATCH /api/v1/transfers/{transferId}/content`;
- `DELETE /api/v1/transfers/{transferId}`;
- `POST /api/v1/transfers/{transferId}/complete`;
- `GET /api/v1/shares/{shareId}/content` with strong ETag and bounded range support.

Transfer ownership is bound to the authenticated paired device and permissions are checked per operation. Physical Windows root paths and native exception details are not exposed over the wire.

Android uses `ACTION_OPEN_DOCUMENT` and `CREATE_DOCUMENT`; content URIs remain capabilities and are never converted to filesystem paths. Upload hashes/counts the source, reopens it and sends bounded chunks with exact `Upload-Offset`. Download streams directly into the selected SAF destination and validates length plus SHA-256-derived ETag before success. Long-running I/O is owned by a non-exported `dataSync` Foreground Service.

## Durable upload recovery — implemented on both sides

### Windows authority — PR #11 merged

PR #11 is merged into `main`. Production upload state is no longer process-local.

Windows durable recovery includes:

- separate versioned SQLite transfer journal;
- persistent idempotency keys and committed offsets;
- per-transfer mutation gates and short device commit fences;
- durable staging capabilities with volume/file identity checks;
- startup readiness reconciliation before the API listener becomes available;
- `write → FlushToDisk → SQLite commit → response` ordering;
- rollback only after durable truncate/flush succeeds;
- rename as the file commit point;
- rename-before-DB recovery using destination file ID, volume, size and SHA-256 proof;
- terminal-first cancel and device revoke ordering;
- persisted opaque share generations so an old root cannot be reopened by path strings;
- bounded orphan inspection and bounded shutdown.

The server-side journal remains the authoritative source of upload progress after restart.

See [Windows durable recovery contract](architecture/durable-transfer-recovery.md).

### Android process-kill recovery — PR #12 merged

PR #12 is merged into `main`.

Android durable upload recovery includes:

- one versioned/bounded app-private recovery journal;
- checked file sync, atomic rename and directory sync commit boundary;
- stable operation ID and idempotency key persisted before server create;
- source name/size/SHA-256 persisted before create;
- server transfer ID and last observed committed offset checkpointed monotonically;
- actual `ContentResolver.persistedUriPermissions` used as the SAF capability authority rather than a JSON boolean alone;
- saved PC identity re-verification through pinned TLS/client identity and `/api/v1/info` before resumed bytes;
- full source re-hash before upload continuation;
- server-ahead/local-behind adoption;
- server-behind/local-ahead fail closed;
- lost create response and ambiguous PATCH/complete recovery;
- durable cancel intent before remote cancellation;
- cancel/completion and stale-writer race handling;
- completion receipt persistence across process recreation;
- corrupt/unknown/oversized/multiple recovery records fail closed;
- Activity/ViewModel recreation cannot downgrade a live Foreground Service transfer to stale resumable state;
- process-killed downloads are intentionally not resumed to the same generic SAF destination.

See [Android durable recovery contract](architecture/android-durable-transfer-recovery.md).

## Automated validation status

PR #12 final HEAD `4c65f0369c96a01f0c1e9cc73a0b3ba2b7e40f5e` passed GitHub Actions CI #231 before merge.

The final CI covered:

- Android formatting, protocol tests, app unit tests, lint and `assembleDebug`;
- Windows restore, format, Release build and tests;
- protocol schema validation, generated-code consistency and `git diff --check`.

The final recovery audit found no remaining Critical, High or merge-blocking Medium issue in the automated/code-auditable boundary. This does **not** substitute for real-device acceptance.

## Operational limits still present

Windows durable journal currently keeps up to 4,096 records and deliberately does not auto-evict idempotency mappings. Active transfers are bounded per device and staging reservation is bounded. Failed/cancelled records may conservatively retain reservation when cleanup cannot be proven.

Automatic journal retention/maintenance is not implemented yet. Do not delete an active `transfers.db` simply to reclaim capacity. Retention must be designed so it does not break idempotency, terminal proof, recovery or staging ownership.

Entry-list pagination is not implemented; the first page is capped and non-empty cursors are rejected.

Windows does not automatically rebind when the selected DHCP/Wi-Fi adapter changes; the user can restart the connection from the tray UI.

Android recovery is user-driven after app reopen. No unrestricted background resurrection scheduler is claimed.

## Required physical-device acceptance

This is the largest remaining MVP gate.

Windows 11 + Android should be exercised together for at least:

- initial QR pairing and comparison-code approval;
- mDNS discovery/re-discovery on real Wi-Fi;
- Android Keystore mTLS requests;
- basic upload and download;
- seekable and nonseekable/reopenable SAF providers;
- providers that accept and reject persistable grants;
- upload process kill before create, during upload and after server completion;
- Android app/process restart and device reboot;
- Windows process restart / PC reboot;
- Wi-Fi interruption and reconnect;
- screen-off and foreground-service timeout behavior;
- multi-GB transfer, including 4 GiB+ where feasible;
- disk-full / staging failure;
- PC sleep/resume;
- NTFS and ReFS where available;
- real mounted-volume rejection behavior;
- Windows private ACL and security-software interference where practical.

Power-loss and filesystem-flush semantics can only be validated meaningfully on real storage; CI cannot prove them.

## Remaining implementation after the core recovery work

These are separate follow-up increments rather than blockers in the durable upload state machine itself:

1. Windows transfer journal retention / maintenance policy and tooling.
2. DHCP/Wi-Fi adapter change auto-rebind.
3. Entry-list pagination.
4. `ACTION_SEND` / `ACTION_SEND_MULTIPLE` UX.
5. text / URL transfer and history UI.
6. Optional bounded recovery scheduler if unattended recovery becomes a product requirement.
7. Release/operations hardening such as protected `main`, required CI checks, consistent explicit state-store ACL policy and optionally pinning third-party Actions to immutable SHAs.

## Continuation order

1. Keep README / implementation status / handoff synchronized with `main`.
2. Create a concrete physical-device acceptance checklist and record evidence rather than treating CI as device acceptance.
3. Run Android ↔ Windows real-device acceptance and fix only findings demonstrated by those tests.
4. Design Windows journal retention/maintenance separately, preserving idempotency and recovery invariants.
5. Add ACTION_SEND/MULTIPLE and text/URL/history as independent product increments.
6. Add auto-rebind/pagination/recovery-scheduler features according to product priority.
