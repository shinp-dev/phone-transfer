# ADR 011: Reproducible lint gates and explicit backup exclusions

Date: 2026-09-07
Status: Accepted.

## Observed failures

After Kotlin formatting was corrected, Android compiled and its three unit tests passed. Lint then reported eleven errors: nine SDK/dependency-update notices, missing data-extraction rules and a missing application icon. The full report is now printed and uploaded even on failure.

## Decision

Keep the selected SDK/toolchain and pinned dependency versions from the initial architecture decision. OldTargetApi, GradleDependency and NewerVersionAvailable are informational in the Android app's lint.xml. They remain visible in reports; upgrades are explicit reviewed changes with compatibility and device testing. All other warnings remain errors. No general baseline, abortOnError override or broad suppression is introduced. This does not waive dependency vulnerability review or future store target-SDK requirements.

Set explicit all-domain exclusions for both Android 12+ cloud backup and device-to-device transfer, and legacy full-backup rules. Keep allowBackup=false. Device identities and transfer/history state must not migrate independently of non-exportable Keystore keys. Future persistence adapters should additionally store sensitive state in noBackupFilesDir. Exporting a user-selected file through SAF remains a separate intentional operation.

Add a native adaptive launcher icon so the installed development app is identifiable.

## Remaining device validation

Verify backup/restore and OEM device migration behavior on supported Android versions before release. XML validation and lint cannot prove OEM behavior or Keystore retention across restore.

## References

- [Android lint severity configuration](https://developer.android.com/studio/write/lint)
- [Android backup and extraction rules](https://developer.android.com/identity/data/autobackup)
