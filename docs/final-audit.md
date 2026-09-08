# Final code / feature audit

Audited: 2026-09-09 JST

Audited `main`: `7c5f85f1c09215d0da24e8d9efe338754a4b17de` (PR #14 merged)

This is the final code-auditable review before physical-device acceptance. It is not a security certification and does not replace real Windows/Android testing.

## Verdict

**Ready for physical-device acceptance.**

No Critical, High, or merge-blocking Medium finding was identified in the current production paths. The remaining MVP gate is physical-device acceptance: real SAF/provider behavior, process/device/PC lifecycle, network interruption, Windows storage semantics and large-file/resource-pressure behavior cannot be proven by CI alone.

Post-merge GitHub Actions CI #242 (`34281143394`) passed on the audited `main` HEAD:

- Windows: restore, format verification, Release build, 164/164 tests passed; 0 build warnings/errors.
- Protocol: schema validation, generated-model drift check and `git diff --check` passed.
- Android: Spotless, protocol/app JVM tests, lint and `assembleDebug` passed. Lint reported 0 errors / 0 warnings; only dependency/API-version hints remained.

## Code-level audit

### Pairing / identity / transport

- Pairing uses a single active, single-use 120-second QR challenge, proof of possession and explicit PC-side comparison-code approval.
- Android persists a Keystore-backed client identity; Windows uses a current-user non-exportable CNG server key.
- Android trusts the QR-originated SPKI pin rather than public-CA fallback. Main API traffic uses mTLS.
- mDNS/NSD results are routing hints only; endpoint changes are accepted only after the stored pin, client identity and stable PC Device ID verify.
- Windows rechecks paired-device authorization on every API request, so a reused TLS connection does not bypass revocation.
- Request/concurrency/body bounds exist on bootstrap and authenticated listeners.

### Windows filesystem containment

- Network paths pass the strict `RelativeSharePath` boundary and are then traversed relative to retained directory handles.
- Configured roots are restricted to local NTFS/ReFS and reject UNC, volume roots, application-state overlap and unsafe reparse ancestry.
- Reparse/junction/symlink/hardlink and root/parent identity replacement paths fail closed.
- Upload staging is private and destination completion uses same-volume no-replace rename.
- Physical root paths and native exception details are not exposed through the file API.

### Durable Windows upload authority

- Windows SQLite journal is the restart authority for upload state.
- Acknowledgement ordering is `write -> FlushToDisk -> journal commit -> response`.
- Ambiguous journal commits are read back and accepted only if the exact expected durable state is proven; otherwise availability is poisoned rather than guessing.
- Startup reconciliation runs before API readiness and repairs/truncates only states that can be proven safe.
- Rename is the file commit point. A crash between rename and DB update can converge to Completed only after destination identity/volume/size/SHA-256 proof.
- Cancel and device-revoke ordering preserve terminal/authorization authority before cleanup.
- Transfer ownership includes the paired-device identity snapshot, not only a caller-supplied transfer ID.

### Android SAF / durable recovery

- SAF URIs remain capabilities and are not converted to filesystem paths or sent to Windows.
- Long-running file I/O is owned by a non-exported `dataSync` Foreground Service.
- Upload operation/idempotency/source metadata is made durable before the corresponding server side effect.
- Actual `persistedUriPermissions`, not only a saved boolean, is the resume-capability authority.
- Resume revalidates PC identity and fully re-hashes the source before sending additional bytes.
- Windows status remains authoritative: server-ahead/local-behind is adopted; server-behind/local-ahead fails closed.
- Lost create/PATCH/complete responses reconcile through stable idempotency and server status rather than blind duplicate writes.
- User cancel intent is durable before remote cancellation. Stale checkpoints cannot clear it, and server Completed wins a cancel/completion race.
- Completion receipt is durable across Android process recreation.
- Corrupt/unknown/oversized/multiple local journal states block new transfer rather than being treated as empty.
- Interrupted downloads are intentionally not resumed to the same generic SAF destination after process death; service/state/UI all enforce that boundary.

### Text / URL

- The implemented direction is Android -> PC only, while the `plainText` / `url` protocol model remains extensible.
- Windows enforces `TextSend`; body/content bounds and strict kind/content validation are applied on both sides.
- URL kind is restricted to absolute HTTP/HTTPS without embedded credentials.
- Receipt never auto-opens a URL. The Windows user must explicitly press the open button, which re-validates the URL.
- Tray notification does not expose message content.
- Response-loss retry reuses the same idempotency key; Windows suppresses duplicate presentation within the active bounded runtime window.

### State / lifecycle / UI

- Live foreground transfer state cannot be replaced by a different operation or downgraded by stale ViewModel restoration.
- Stale completion/failure/cancel callbacks are operation-ID guarded.
- File-transfer cancellation and text-operation cancellation UX are separated.
- Windows UI exposes the implemented pairing/share/file/text operations and no longer displays the obsolete “Android SAF UI under development” message.

## Feature-level audit

The current MVP scope has an end-to-end UI path for:

1. selecting the PC LAN adapter and receive folder;
2. QR pairing and explicit comparison-code approval;
3. saved-PC rediscovery/reconnection;
4. Android browsing of the PC share;
5. Android -> PC file upload;
6. PC -> Android file download;
7. Android -> PC text and URL delivery;
8. transfer progress/cancel;
9. Android process-kill upload recovery;
10. Windows process-restart upload recovery.

The code-level contract intentionally does **not** claim process-kill continuation of an interrupted download.

## Non-blocking findings / debt

These do not block physical MVP acceptance, but should stay visible:

- **Low — legacy process-local transfer code remains:** the older Android direct-upload helper and Windows `BasicFileTransferService` upload implementation still exist, while production upload uses the durable paths. They are not currently wired as production upload authority, but removing or clearly isolating them later would reduce accidental-rewire risk.
- **Low — text endpoint protocol polish:** malformed ASP.NET request cases can fall through to a generic service-unavailable mapping, and JSON Content-Type is not independently enforced before strict JSON parsing. The shipped Android client sends the expected JSON and the server still bounds/parses/authorizes the body.
- **Low — pairing availability:** bootstrap rate limiting is deliberately small/global; a same-LAN abuser can degrade pairing availability temporarily but cannot bypass proof/approval/mTLS.
- **Operational hardening:** `main` is currently unprotected and CI Actions are referenced by release tags rather than immutable SHAs. This is repository/release hygiene, not a runtime containment bypass.
- **Android maintenance hints:** current CI reports only non-blocking version/deprecation hints (for example NSD/notification APIs and newer dependency versions).
- **Release packaging:** source builds and a debug APK are available, but installer/signing/release-distribution polish is separate from the core runtime MVP acceptance.

## Physical-only evidence still required

Run [physical-device acceptance](physical-device-acceptance.md). The minimum gate covers pairing/reconnect, both file directions, text/URL, Android process-kill upload recovery, Windows process-restart recovery, interrupted-download fail-closed behavior and one network-loss recovery. Extended testing then covers device/PC reboot, sleep/screen-off, provider variants, multi-GB/disk-full and NTFS/ReFS/mounted-volume behavior.
