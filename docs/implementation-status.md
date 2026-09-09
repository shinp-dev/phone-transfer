# Implementation status

Updated: 2026-09-09

Phone Transfer remains pre-MVP because full physical-device acceptance is not complete. The core path is substantially implemented: pairing, mDNS discovery, shared-folder configuration, Windows handle-safe filesystem, authenticated file transfer, Android SAF/Foreground Service ownership, Windows durable upload recovery, Android process-kill upload recovery, and Android → PC plain-text/URL sending are implemented in the current development line.

See [development handoff](handoff.md) for the next work order.

## Phase 1 — foundation complete

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

Windows provides single-use 120-second QR challenges, ECDSA P-256 proof verification, local comparison-code approval, signature-authorized polling, isolated HTTPS pairing, SQLite device registration/revocation, current-user non-exportable CNG server identity, request-by-request revocation checks and bounded shutdown.

Android provides QR validation/scanning, Android Keystore identity, pinned HTTPS registration, signed polling, comparison-code UI, mTLS `/api/v1/info` verification, saved-PC persistence and local removal.

Physical Pixel 8a acceptance confirmed QR approval, saved-PC persistence and repeated client-certificate authentication over TLS 1.3 after authorizing both `DIGEST_SHA256` and `DIGEST_NONE` on newly generated Android Keystore EC keys. Conscrypt signs a TLS-computed digest through `NONEwithECDSA`; limiting the key to `DIGEST_SHA256` allowed pairing proofs but caused the first mTLS API handshake to fail. Basic PC-to-Android download and Android-to-PC upload to the Windows share root are also confirmed. Wider interruption, provider, text-delivery, restart and revocation acceptance remains pending.

Windows advertises `_phone-transfer._tcp` on the selected private IPv4 interface and Android discovers it with `NsdManager`. Discovery data is routing metadata only; endpoint changes are saved only after the stored SPKI pin, client identity and stable Device ID verify.

## Phase 3 — authenticated handle-safe file transfer implemented

Windows can select, persist and clear one local NTFS/ReFS receive root. UNC paths, volume roots, overlap with application data and unsafe reparse ancestry are rejected. Traversal is based on retained directory handles; reparse points, junctions, symlinks, hardlinks, identity replacement races and unsafe mount/path transitions fail closed. Staging is private and completion uses same-volume atomic no-overwrite rename.

The authenticated file API includes share/listing, durable upload create/status/chunk/cancel/complete and stable-handle download with strong ETag/range support. Transfer ownership is bound to the authenticated paired device and permissions are checked per operation. Physical root paths and native exception details are not exposed over the wire.

Android uses `ACTION_OPEN_DOCUMENT` and `CREATE_DOCUMENT`; content URIs remain capabilities and are never converted to filesystem paths. Long-running file I/O is owned by a non-exported `dataSync` Foreground Service.

Physical testing found that a share-root upload correctly supplies an empty remote directory path, but transfer-intent admission originally rejected that value and misleadingly reported `LOCAL_JOURNAL_UNAVAILABLE`. Upload admission now permits only that intentional empty root path; other required extras and download paths remain non-empty. See [Android root-upload investigation](known-issues/android-root-upload-local-journal-unavailable.md).

## Durable upload recovery — implemented on both sides

### Windows authority — PR #11

Production upload state is durable SQLite rather than process-local memory. The main invariants are:

- persistent idempotency keys and committed offsets;
- startup reconciliation before API readiness;
- `write → FlushToDisk → SQLite commit → response` ordering;
- rollback only after durable truncate/flush succeeds;
- rename as the file commit point;
- rename-before-DB recovery using destination identity/volume/size/SHA-256 proof;
- terminal-first cancel and device-revoke ordering;
- persisted opaque share generations so old roots are never rediscovered by string path;
- bounded orphan inspection and bounded shutdown.

The Windows journal remains the authoritative source of upload progress after restart. See [Windows durable recovery contract](architecture/durable-transfer-recovery.md).

### Android process-kill recovery — PR #12

Android persists one bounded app-private recovery record or completion receipt. It keeps a stable operation/idempotency identity, source name/size/SHA-256, server transfer ID and last observed committed offset. Actual `ContentResolver.persistedUriPermissions`, not a saved boolean alone, is the SAF capability authority.

Before resumed bytes, Android re-verifies the saved PC through pinned TLS/client identity and `/api/v1/info`, re-hashes the full source, and reconciles against Windows state. Server-ahead/local-behind is adoptable; server-behind/local-ahead fails closed. Lost create/PATCH/complete responses, durable cancel intent, cancel-vs-complete races, completion receipts and corrupt local journals are handled explicitly.

Interrupted downloads are intentionally not resumed to the same generic SAF destination after process death. See [Android durable recovery contract](architecture/android-durable-transfer-recovery.md).

## Android → PC text / URL — implemented in PR #14

This increment uses the existing Protocol v1 `SendText` / `TextEntry` wire model without changing OpenAPI or generated DTOs.

Implemented boundary:

- delivery direction is Android → PC only;
- authenticated `POST /api/v1/text` is served on the existing mTLS API listener;
- `TextSend` permission is enforced server-side;
- kinds remain `plainText` and `url` so future history/reverse delivery can extend the same model;
- content is limited to 65,536 UTF-16 code units and the JSON request remains under the protocol body bound;
- URL kind accepts only absolute `http` / `https` URLs without embedded credentials;
- Android retries one raw transport failure with the same per-device idempotency key;
- Windows suppresses duplicate presentation for the same key/payload within the active runtime and rejects key reuse with different content;
- Windows shows the latest received value and lets the user copy it;
- URL receipt never auto-launches a browser; opening happens only after an explicit PC-side button click;
- tray notifications are generic and do not expose the received text itself.

User-visible history, PC → Android delivery and Android `ACTION_SEND` / `ACTION_SEND_MULTIPLE` integration remain separate increments. The Protocol v1 extensibility for those features is retained.

## Automated validation

CI validates:

- Android Spotless, generated protocol tests, app JVM unit tests, lint and `assembleDebug`;
- Windows restore, `dotnet format`, Release build and xUnit tests;
- protocol schema validation, generator drift and `git diff --check`.

The text/URL increment adds Android rule tests plus a real-Kestrel Windows mTLS integration test for idempotent delivery, URL validation and permission enforcement. CI is not a substitute for real-device acceptance.

## Operational limits still present

- Windows durable transfer journal keeps up to 4,096 records and does not auto-evict idempotency mappings.
- Automatic transfer-journal retention/maintenance is not implemented. Do not delete an active `transfers.db` to reclaim capacity.
- Text/URL has no user-visible persistent history in this increment; the duplicate-suppression window is process-local and bounded.
- Entry-list pagination is not implemented; the first page is capped and non-empty cursors are rejected.
- Windows does not automatically rebind when the selected DHCP/Wi-Fi adapter changes; the user can restart the connection from the tray UI.
- Android durable recovery remains user-driven after app reopen; no unrestricted background resurrection scheduler is claimed.

## Required physical-device acceptance

This is the largest remaining MVP gate. Windows 11 + Android should be exercised together for at least:

- QR pairing / comparison code and real-Wi-Fi mDNS;
- Android Keystore mTLS requests;
- basic upload/download (Pixel 8a ↔ Windows share root confirmed);
- seekable and nonseekable/reopenable SAF providers;
- providers that accept and reject persistable grants;
- upload process kill before create, during upload and after server completion;
- Android app/process restart and device reboot;
- Windows process restart / PC reboot;
- Wi-Fi interruption/reconnect, screen-off and FGS timeout;
- multi-GB transfer, disk-full/staging failure and PC sleep/resume;
- NTFS/ReFS and real mounted-volume rejection where available;
- Android → PC plain text and URL send;
- tray-hidden receipt notification;
- copy behavior;
- URL does not auto-open and opens only after explicit user action.

Power-loss/filesystem-flush semantics can only be validated meaningfully on real storage; CI cannot prove them.

## Remaining implementation

Separate follow-up increments:

1. Windows transfer-journal retention / maintenance policy and tooling.
2. DHCP/Wi-Fi adapter-change auto-rebind.
3. Entry-list pagination.
4. Android `ACTION_SEND` / `ACTION_SEND_MULTIPLE` UX, including sharing text/URLs from other apps.
5. Optional text history and PC → Android delivery if product requirements later need them.
6. Optional bounded recovery scheduler.
7. Release/operations hardening such as protected `main`, required CI checks, consistent explicit state-store ACL policy and optional immutable Action SHA pinning.

## Continuation order

1. Finish PR #14 CI/self-audit and merge only after explicit approval.
2. Create a concrete physical-device acceptance checklist with evidence fields.
3. Run Android ↔ Windows real-device acceptance, including text/URL delivery, and fix demonstrated findings in separate PRs.
4. Design Windows transfer-journal retention/maintenance separately without weakening idempotency/recovery invariants.
5. Add ACTION_SEND/MULTIPLE or other UX according to product priority.
