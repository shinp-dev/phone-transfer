# Android root upload reported `LOCAL_JOURNAL_UNAVAILABLE`

Updated: 2026-09-09

## Status

Resolved on the physical Pixel 8a. Android-to-PC upload to the configured share root now completes without manually deleting the existing Android recovery journal or resetting app data.

## User-visible symptom

PC-to-Android download completed, but selecting a file on Android and sending it to the root of the Windows share stopped immediately with:

```text
転送復旧データを検証できないため、新しい転送を開始しません。
アプリの復旧データを確認するまで安全側に停止します: LOCAL_JOURNAL_UNAVAILABLE
```

The message suggested that the app-private recovery journal was unavailable or corrupt. Inspection showed that `files/transfer-operations.json` existed, was readable, and contained a valid completed download receipt. No temporary or backup journal indicated a failed atomic replacement.

## Root cause

An upload to the share root legitimately uses an empty remote directory path. `HomeViewModel` passed that empty string through `FileTransferService.startUpload`, but `persistNewOperation` read every string extra through a helper that rejected empty strings as missing intent data.

The resulting `INVALID_TRANSFER_INTENT` exception occurred before the new upload was inserted. A broad admission error handler then presented it as `LOCAL_JOURNAL_UNAVAILABLE`, even though the journal itself was healthy.

## Resolution

The transfer service now allows an empty remote path only while admitting a new upload. Device ID, share ID, URI and operation ID remain non-empty, and downloads still require a non-empty remote file path.

A JVM regression test records both sides of the boundary:

- an explicitly allowed empty share-root value is accepted;
- missing values and unexpected empty values still fail with `INVALID_TRANSFER_INTENT`.

No journal deletion or app-data reset is part of the fix.

## Physical validation

Validation used the existing paired Pixel 8a and the Windows Debug app on the same Wi-Fi network:

1. The fixed APK was installed with application data preserved.
2. The pre-existing Android recovery journal remained readable.
3. PC-to-Android download completed.
4. Android-to-PC upload to the share root completed.
5. The Windows receive root contained the newly transferred 12,134,570-byte JPEG.

Android `spotlessCheck`, the focused regression test, `lintDebug` and `assembleDebug` also completed successfully.
