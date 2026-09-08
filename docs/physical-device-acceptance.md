# Physical-device acceptance

Updated: 2026-09-09 JST

This checklist is the final MVP gate after the code/CI audit. Record actual results; do not turn an untested row into PASS because CI is green.

## Minimum flow

The minimum flow is intentionally small enough to run in one session while still exercising the product's important trust and recovery boundaries.

### Preconditions

- Windows 11 PC and Android 9+ device on the same private Wi-Fi/LAN.
- Current source: `main` at or after `7c5f85f1c09215d0da24e8d9efe338754a4b17de`.
- Windows has .NET 10 SDK/runtime available.
- Android device has USB debugging/ADB available for the reproducible process-kill step.
- Create a disposable local NTFS folder such as `C:\PhoneTransferTest` and do not use valuable files for the first run.
- Prepare one small file and one larger file (roughly 200–500 MB is enough to leave time to interrupt an upload).

Build/run from the repository root:

```powershell
dotnet run --project apps/windows/PhoneTransfer.App
```

```sh
gradle -p apps/android :app:assembleDebug
adb install -r apps/android/app/build/outputs/apk/debug/app-debug.apk
```

Android application ID:

```text
com.shinpstudio.phonetransfer
```

### M01 — PC start and share

1. Start the Windows app.
2. Select the active private LAN adapter.
3. Set `C:\PhoneTransferTest` as the receive folder.

PASS when the server starts without an error and the share remains selected. mDNS advertisement should be available on a normal private LAN; if mDNS is unavailable, record that separately rather than treating QR pairing itself as failed.

### M02 — Pair and reconnect

1. On Windows press **スマホを登録**.
2. Scan the QR on Android.
3. Confirm that the comparison number shown on both devices matches.
4. Approve on Windows.
5. On Android connect to the saved PC.
6. Close and reopen the Android app once, then connect again.

PASS when the PC is saved once, reconnect succeeds, and no certificate/Device-ID mismatch is shown.

### M03 — File transfer in both directions

1. Android: open the PC share and upload the small test file.
2. Confirm the file appears in `C:\PhoneTransferTest` and the Android UI reports completion.
3. Android: select a PC file and save it to a new Android destination.
4. Open/inspect the downloaded file.

PASS when both directions complete and the resulting content is correct. The protocol already performs SHA-256/length validation; for stronger evidence, also compare an external hash where practical.

### M04 — Text and URL

1. Send a recognizable plain-text value from Android.
2. Confirm Windows shows it and **コピー** copies the same value.
3. Send an `https://example.com/...` URL.
4. Confirm the browser does **not** open merely because the URL was received.
5. Press **URLを開く** and confirm the default browser opens that URL.
6. Hide the Windows window to the tray and send one more value.

PASS when content arrives, copy works, URL opening is explicit-only, and the tray notification is generic and does not include the received body.

### M05 — Android process-kill upload recovery

1. Start uploading the 200–500 MB file and wait until progress is clearly above zero.
2. Without pressing the app's cancel button, execute:

```sh
adb shell am force-stop com.shinpstudio.phonetransfer
```

3. Relaunch Phone Transfer manually.
4. Confirm an interrupted upload is shown as recoverable.
5. Press the safe-resume action.

PASS when identity/SAF/source checks succeed, the upload continues to completion, only one final destination exists, and no partial/duplicate destination is presented as complete.

`force-stop` is intentionally harsher than an ordinary process reclaim. User relaunch is required afterwards; that is acceptable for this user-driven recovery design.

### M06 — Windows process-restart upload recovery

1. Start another large Android -> PC upload.
2. After progress advances, terminate the Windows Phone Transfer process from Task Manager without cancelling the transfer normally.
3. Relaunch the Windows app and choose the same active adapter. Keep the same configured share.
4. Reopen/reconnect Android and resume if the Android UI requests it.

PASS when Windows startup reconciliation completes, the upload safely resumes/converges, and exactly one correct completed destination remains.

### M07 — Interrupted download fails closed

1. Start downloading a reasonably large PC file to Android.
2. While it is in progress, force-stop the Android app again.
3. Relaunch the app.

PASS when the interrupted **download is not offered as resumable to the same SAF destination**. The UI should require cancelling/clearing the interrupted receive and starting a fresh receive with a newly selected destination. A partial provider-owned destination may exist, but it must never be reported as completed.

### M08 — One network interruption

1. Start a large upload.
2. Turn Android Wi-Fi off long enough for the transfer to fail/interruption state, then turn Wi-Fi back on.
3. Reopen/reconnect if necessary and resume through the app's recovery UI.

PASS when no corrupt completed file appears during the outage and the final resumed transfer completes correctly after connectivity returns.

## Minimum MVP pass criteria

All M01–M08 must PASS, or any failure must be understood and fixed/retested before declaring the physical MVP gate complete.

Record results in this form:

| ID | Windows / Android | Result | Evidence | Notes |
| --- | --- | --- | --- | --- |
| M01 |  | PASS / FAIL | screenshot/log |  |
| M02 |  | PASS / FAIL |  |  |
| M03 |  | PASS / FAIL |  |  |
| M04 |  | PASS / FAIL |  |  |
| M05 |  | PASS / FAIL |  |  |
| M06 |  | PASS / FAIL |  |  |
| M07 |  | PASS / FAIL |  |  |
| M08 |  | PASS / FAIL |  |  |

## Extended acceptance after the minimum flow

These are important before a broader release but need not all be in the first smoke session:

- Android device reboot during/reafter interrupted upload;
- Windows PC reboot during interrupted upload;
- Windows sleep/resume;
- Android screen-off during a long transfer;
- Android notification permission allowed/denied and dataSync FGS timeout behavior;
- SAF providers that do and do not grant persistable read permission;
- nonseekable but reopenable SAF providers;
- provider changes/replaces the selected source after the first hash;
- multi-GB and, where feasible, 4 GiB+ transfer;
- Windows destination/staging disk-full behavior;
- NTFS and ReFS;
- real mounted-volume/reparse rejection;
- security software/ACL interference with the private Windows state/staging area;
- PC DHCP/Wi-Fi adapter change. Current product behavior requires manual reconnect/rebind rather than automatic adapter migration;
- paired-device revocation while idle and while a transfer is active;
- multiple text retries around a brief network failure; duplicate presentation suppression is only guaranteed within the active Windows runtime's bounded window.

## Failure handling

For every FAIL, capture:

1. exact Windows and Android versions;
2. source/destination provider or filesystem;
3. step that failed;
4. UI error code/message;
5. whether a partial final file exists;
6. whether the operation is resumable/cancellable after restart;
7. screenshots and relevant logs without publishing pairing tokens, certificate material, SAF URIs or private filenames unnecessarily.

Create a separate fix branch/PR from the latest `main`, add a regression test where the failure is code-reproducible, rerun CI, then rerun the failed physical case.
