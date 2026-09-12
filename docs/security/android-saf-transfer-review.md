# Android SAF transfer security review

Reviewed: 2026-09-08

Scope: `feature/android-saf-transfer` Android-only transport/SAF increment. Windows handle-safe containment and the PR #9 server API are treated as fixed lower-layer boundaries and are not weakened here.

## Invariants

- `PairingRepository` remains pairing/discovery ownership only. File API calls live in `FileTransferRepository`.
- Every file request uses the saved SPKI pin plus Android Keystore client identity through the existing `PinnedTls` client. No public-CA fallback, proxy, redirect or cleartext path is introduced.
- SAF `Uri` values remain Android capabilities. They are passed only to `ContentResolver` and are never converted to filesystem paths or sent to Windows.
- A SAF display name is validated against the same Windows wire-level ambiguous-name rules before it becomes a remote relative path. This is a syntax boundary only; Windows handle containment remains authoritative.
- Upload does not trust provider metadata for content integrity. It streams the selected document to compute actual byte count and SHA-256, then sends the same values in `CreateTransfer`. Windows independently hashes staging before no-replace completion.
- A source provider that changes bytes between prehash and upload cannot create a falsely successful file: Windows completion rejects the digest mismatch.
- PATCH response loss is not blindly retried. The client reads the process-local transfer status and accepts the chunk only when the committed offset equals the expected next offset.
- Complete response loss is reconciled through transfer status; a server-reported `completed` state wins over transport ambiguity.
- Download success requires both declared Content-Length and the strong SHA-256 ETag to match bytes written through `ContentResolver`.
- Failed/cancelled download output is best-effort truncated and never reported as completed. Atomic rollback is provider-dependent and remains a physical acceptance item.
- Transfer work is owned by a non-exported `dataSync` Foreground Service. Activity/ViewModel do not own the long-running network/file streams.
- The service accepts only explicit in-app intents carrying device/share/path/URI identifiers; secrets, pins and private keys are not placed in intents or notifications.

## Deliberate limitations

- v1 requires SHA-256 and total size before upload creation. The source URI is therefore opened twice. Seek is not required, but a provider that cannot reopen the same selected document is unsupported in this increment.
- Android process kill is not restart-resume. The Foreground Service is process-local and Windows staging reconciliation is still deferred to durable recovery.
- SAF providers do not offer a universal atomic rename/rollback primitive for an already-created destination URI. Hash mismatch and short reads fail visibly, but provider refusal to truncate can leave a partial local document.
- Only one foreground transfer is owned by the Android service at a time. The in-app picker may persist up to 100 uniquely identified sources and feeds them through a separate durable sequential queue. ACTION_SEND/MULTIPLE share-sheet integration remains later UX work.
- Full download is used by the Android client in this increment. Windows range/ETag resume remains available for the future durable state machine.

## Required verification before merge

- Spotless, Android unit tests, lintDebug and assembleDebug must pass.
- Confirm Android manifest contains `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_DATA_SYNC`, non-exported `dataSync` service, and no storage/path permissions are added for SAF.
- Confirm no code calls `Uri.getPath()` or otherwise attempts SAF-to-filesystem path resolution.
- Confirm upload and download use the existing pinned mTLS boundary and do not log URI, remote path, filename, pin or certificate material.
- Physical acceptance remains required for real DocumentsUI/cloud providers, notification denied/allowed behavior, screen off, service timeout, Wi-Fi interruption and process kill.
