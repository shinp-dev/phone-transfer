# Pixel 8a / Windows physical mTLS pairing failure investigation

Updated: 2026-09-09

## Status

Resolved on the physical Pixel 8a. QR registration and PC-side approval originally completed, but the Android app failed the first authenticated API request and did not save the paired PC. The failure occurred during the TLS handshake on the API listener, before an HTTP request reached the application.

The Android Keystore EC key authorized only `DIGEST_SHA256`. Conscrypt requires `NONEwithECDSA` when it signs a TLS-computed digest with an opaque Android Keystore key. Authorizing both `DIGEST_SHA256` and `DIGEST_NONE`, regenerating the test identity explicitly, and pairing again fixed the failure without changing TLS versions or certificate validation.

References:

- [Android `KeyGenParameterSpec.Builder.setDigests` documentation](https://developer.android.com/reference/android/security/keystore/KeyGenParameterSpec.Builder.html#setDigests\(java.lang.String...\))
- [Android 35 Conscrypt delegated EC signing implementation](https://android.googlesource.com/platform/prebuilts/fullsdk/sources/+/refs/heads/androidx-constraintlayout-release/android-35/com/android/org/conscrypt/CryptoUpcalls.java#64)

## Test environment

- Windows PC running the Debug `PhoneTransfer.App` build.
- Google Pixel 8a running the Debug Android APK installed over USB.
- Both devices connected to the same `Denshi-Ap` network.
- PC address during the latest reproduction: `10.220.3.162/16`.
- Pixel address during the latest reproduction: `10.220.1.245/16`.
- Pairing listener: TCP 58442.
- Authenticated API listener: TCP 58443.

The PC adapter was displayed as `Denshi-Ap 2` while the phone displayed `Denshi-Ap`; the different display names did not indicate different IP networks.

## User-visible symptom

1. Scan the Windows QR code from Android.
2. Submit the Android registration request.
3. Approve the matching comparison code on Windows.
4. Android eventually displays `接続または保存に失敗しました。LANとQR期限を確認してください。再登録する場合はPC側の登録を解除してください。`.

Windows nevertheless contains an active paired-device registration. Android has generated and retained its device ID and client certificate, but `paired-pcs.json` is not created because the post-approval API verification does not complete.

## Confirmed observations

### Network and listener state

- TCP connectivity from the Pixel to the PC was confirmed for both ports 58442 and 58443.
- An earlier run of the Windows app remained bound to an obsolete address, `10.40.112.54`. Restarting the app corrected the listeners to `10.220.3.162`.
- Fixing the stale bind did not fix the post-approval failure.
- Pairing traffic on port 58442 succeeds over HTTPS/HTTP/2: the initial POST returns 202 and signed status polling returns 200.
- No HTTP request is logged on port 58443. The failure is therefore before ASP.NET request processing and is not a file-share or save-path error.

### Registration and client identity

- Windows registered Android device ID `f24f1066-e631-4bbe-8118-13cfd623fa1b` with all current permissions and `revoked = false`.
- The SHA-256 fingerprint stored by Windows is `443114b778a93ddab6e83d497e7e321b42f7a2c10ad64d2f95abbaa786c0b820`.
- The certificate retained by Android has the same SHA-256 fingerprint.
- The client certificate is a self-signed ECDSA P-256 certificate with digital-signature key usage and client-authentication EKU. It is currently valid and has `CA = false`.
- Registration proof verification succeeds on Windows. This proves that the Android private key used for the proof corresponds to the public key in the submitted certificate.
- Android also checks the persisted certificate public key against the Android Keystore entry and verifies the certificate signature when loading the identity.

### TLS failure evidence

Android's client key manager is called and returns the expected material:

```text
chooseClientAlias keyTypes=RSA, EC
getPrivateKey alias=client
getCertificateChain alias=client
```

Immediately afterward, Android reports:

```text
javax.net.ssl.SSLHandshakeException:
Read error ... I/O error during system call
```

At the same time, Kestrel reports:

```text
Failed to authenticate HTTPS connection.
System.IO.IOException: Received an unexpected EOF or 0 bytes from the transport stream.
```

The configured Kestrel `ClientCertificateValidation` callback is not reached. The connection ends before the application can compare the presented certificate with the registered-device database.

These messages establish a TLS-handshake failure, but do not identify the selected TLS version or conclusively identify which peer initiated the close.

## Experiments already performed

### Restart after network-address change

The Windows app was restarted after finding the stale `10.40.112.54` listener. It then listened on the correct `10.220.3.162` address. Pairing remained successful and the authenticated API handshake still failed.

### Windows current-user peer trust

The exact Pixel client certificate was temporarily imported into `Cert:\CurrentUser\TrustedPeople` with SHA-1 thumbprint:

```text
C1C92D45BA677603136285D67385EA0F128A1E46
```

`certutil -user -verify` then reported the certificate as peer-trusted. The Windows app was restarted and the same failure was reproduced. This makes a simple missing current-user peer-trust entry insufficient to explain the failure.

The temporary certificate is still a local diagnostic artifact and must be removed after investigation:

```powershell
Remove-Item -LiteralPath 'Cert:\CurrentUser\TrustedPeople\C1C92D45BA677603136285D67385EA0F128A1E46'
```

### Additional diagnostics

Temporary local instrumentation exposed the original Android exception, client-key-manager calls, Kestrel HTTPS authentication logs and the certificate hash seen by the validation callback. The callback did not run in the reproductions above.

Windows Schannel did not emit a corresponding event in the System log under the current logging configuration. Windows `pktmon` was considered for a packet-level trace, but capture could not be started without an elevated session.

### Changes deliberately not applied

- `AllowAnyClientCertificate` was not adopted. Accepting arbitrary client certificates during TLS and relying only on later middleware would broaden the trust boundary and needs an explicit security design, not a diagnostic shortcut.
- TLS 1.2-only client configuration was prepared as a possible diagnostic but was not installed on the Pixel and was reverted before testing. Current evidence therefore does not establish a TLS 1.3 interoperability defect.

## Build and test notes

- Windows Debug build completed with zero warnings and zero errors.
- Android `assembleDebug` completed successfully with JDK 17.
- The APK installed successfully on the Pixel 8a.
- Android JVM tests ran 77 tests with 19 failures caused by `java.nio.file.AccessDeniedException` in Windows-hosted durable-journal tests. These failures appear separate from the physical TLS handshake and should be tracked independently rather than treated as evidence for this issue.

Local build outputs used during diagnosis:

- Windows: `apps/windows/PhoneTransfer.App/bin/Debug/net10.0-windows/PhoneTransfer.App.exe`
- Android: `apps/android/app/build/outputs/apk/debug/app-debug.apk`

## Confirmed root cause and resolution

The original key was generated with:

```kotlin
.setDigests(KeyProperties.DIGEST_SHA256)
```

That key could create the `SHA256withECDSA` pairing proof, but could not perform Conscrypt's raw `NONEwithECDSA` operation for the TLS client `CertificateVerify`. Key-manager lookup therefore succeeded before the TLS handshake ended during private-key use.

New identities now authorize both required operations:

```kotlin
.setDigests(
    KeyProperties.DIGEST_SHA256,
    KeyProperties.DIGEST_NONE
)
```

Because Android Keystore authorizations are fixed when the key is created, the old test identity was not silently replaced. The PC registration was removed explicitly, Android app data was cleared explicitly, and a new identity was registered.

After the fix, Kestrel recorded the new registered certificate as `authorized=True` and established repeated connections using `Tls13`. Android saved the paired PC successfully. TLS 1.2 fallback was not needed or tested.

The final Android build, with temporary diagnostic logging removed, was installed over the successful identity and reconnected correctly from the saved-PC UI.

The temporary old client certificate previously added to `CurrentUser\TrustedPeople` was no longer present when cleanup was checked.

## Remaining physical acceptance

The pairing-specific failure is resolved. Continue the wider acceptance checklist with:

1. Android-to-PC text/URL send.
2. File listing, upload and download.
3. PC-side registration revocation followed by a rejected Android request.
4. Reconnection after app restart, device restart and Wi-Fi interruption.

## Acceptance condition

Pairing and initial authenticated connection now satisfy:

- approval is followed by a successful mTLS `/api/v1/info` request;
- Android saves the paired PC;
- repeated API requests authenticate with the registered client identity; and
- no global or accept-any certificate trust bypass is introduced.

File transfer, text delivery and revocation behavior remain part of the broader physical-device acceptance work rather than this pairing defect.
