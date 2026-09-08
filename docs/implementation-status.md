# Implementation status

Updated: 2026-09-09 JST

Current audited main: `7c5f85f1c09215d0da24e8d9efe338754a4b17de` (PR #14 merged)

Phone Transfer is **code/CI ready for physical-device acceptance**. It is still pre-MVP only because the real Windows/Android acceptance gate has not yet been executed and recorded.

See [final audit](final-audit.md), [physical-device acceptance](physical-device-acceptance.md) and [handoff](handoff.md).

## Implemented product boundary

### Pairing / discovery / identity

- Windows 120-second single-use QR challenge and explicit comparison-code approval.
- Android Keystore client identity and Windows current-user non-exportable CNG server identity.
- SPKI-pinned HTTPS during registration and mTLS on the authenticated API.
- Windows mDNS + Android NSD rediscovery; discovered endpoints remain routing hints until pin/client identity/Device ID re-verification succeeds.
- Persistent paired-device registry and request-by-request revocation checks.

### Handle-safe Windows share

- One configurable local NTFS/ReFS receive root.
- UNC, volume root, application-state overlap and unsafe reparse ancestry rejected.
- Retained-handle traversal with reparse/junction/symlink/hardlink and identity-race fail-closed behavior.
- Private staging and same-volume no-overwrite final rename.
- Physical Windows paths never exposed over the wire.

### File transfer

- Authenticated share listing.
- Android SAF upload and download.
- Non-exported `dataSync` Foreground Service owns long-running Android transfer I/O.
- Windows stable-handle download with strong SHA-256 ETag and bounded range support.
- Transfer progress and explicit cancellation UI.

### Durable upload recovery

Windows:

- durable SQLite transfer journal;
- persistent idempotency and committed offsets;
- startup reconciliation before API readiness;
- `write -> FlushToDisk -> journal commit -> response` ordering;
- crash-gap reconciliation around rename using destination identity/volume/size/SHA-256 proof;
- terminal-first cancellation and authorization-first device revoke ordering;
- opaque persisted share generations.

Android:

- bounded app-private durable operation/completion journal;
- operation/idempotency/source identity persisted before server create;
- actual persisted SAF read grant checked before resume;
- PC identity re-verification and full source re-hash before resumed bytes;
- Windows status remains offset/state authority;
- lost create/PATCH/complete response reconciliation;
- durable user-cancel intent and cancel/completion race handling;
- completion receipt across process recreation;
- corrupt/unsupported journal fail-closed behavior;
- interrupted download intentionally not resumed to the same generic SAF destination after process death.

### Android -> PC text / URL

- authenticated `POST /api/v1/text` on the existing mTLS API;
- server-side `TextSend` permission enforcement;
- `plainText` and `url` kinds with 65,536-character content bound and 128 KiB JSON bound;
- absolute HTTP/HTTPS URL only; embedded credentials rejected;
- Android transport retry reuses the same idempotency key;
- bounded process-local Windows duplicate-presentation suppression;
- latest received content display and copy;
- URL never auto-opens; explicit PC-side open action only;
- generic tray notification without message-body disclosure.

## Final automated validation

Post-merge main CI #242 (`34281143394`) completed successfully on `7c5f85f1c09215d0da24e8d9efe338754a4b17de`.

- Windows: restore, format, Release build, **164 passed / 0 failed / 0 skipped**, 0 build warnings/errors.
- Protocol: schema validation, generated check and `git diff --check`.
- Android: Spotless, JVM tests, lint and `assembleDebug`; lint had 0 errors / 0 warnings and 9 non-blocking hints.

The final repository/code audit found no Critical, High or merge-blocking Medium in the code-auditable current production boundary. See [final audit](final-audit.md).

## Current MVP gate

Run [physical-device acceptance](physical-device-acceptance.md).

The minimum M01–M08 flow verifies:

1. PC startup/share;
2. QR pair + reconnect;
3. upload/download;
4. text/URL and explicit URL open;
5. Android process-kill upload recovery;
6. Windows process-restart upload recovery;
7. interrupted download stays non-resumable;
8. one Wi-Fi interruption/recovery.

Until those results are recorded, do not describe the product as physically validated.

## Non-blocking operational / maintenance items

These are separate follow-up increments, not current core-MVP code blockers:

1. Windows transfer-journal retention / maintenance policy and tooling.
2. DHCP/Wi-Fi adapter-change automatic rebind.
3. Entry-list pagination.
4. Android `ACTION_SEND` / `ACTION_SEND_MULTIPLE` UX.
5. Optional persistent text history and PC -> Android delivery.
6. Optional bounded unattended recovery scheduler.
7. Release/operations hardening: protected `main`, required CI checks, explicit state-store ACL consistency and optional immutable Action SHA pinning.
8. Release packaging/signing/installer polish for public distribution.
9. Later removal/isolation of legacy process-local upload helpers that are no longer the production upload authority.

## Known deliberate limits

- Windows transfer journal currently has a bounded record capacity and no automatic retention/eviction policy.
- Text duplicate suppression is bounded/process-local and is not a durable message queue.
- User-visible persistent text history is not implemented.
- PC -> Android text delivery is not implemented.
- Android durable recovery is user-driven after app reopen; unrestricted background resurrection is not claimed.
- Interrupted download continuation to the same SAF destination is intentionally not implemented.
