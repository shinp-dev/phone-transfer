# Pixel 8a / Windows physical mTLS pairing failure investigation

Updated: 2026-09-09

## Status

Open. QR registration and PC-side approval complete, but the Android app fails the first authenticated API request and does not save the paired PC. The failure occurs during the TLS handshake on the API listener, before an HTTP request reaches the application.

This note separates observed facts from hypotheses. TLS 1.2 has not yet been tested, and no broad certificate-validation bypass has been applied.

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

## Current hypotheses

The evidence supports investigation of these possibilities, but does not yet prove any of them:

1. TLS 1.3 client-certificate interoperability between Android Conscrypt/Android Keystore and Windows Schannel/Kestrel.
2. Android failing while producing or sending the TLS client `CertificateVerify`, despite the same key successfully signing the pairing proof.
3. Windows Schannel terminating client-certificate processing before Kestrel's managed validation callback is invoked.
4. A protocol or signature-scheme mismatch that is hidden by the generic EOF/read-error messages.

The shared-folder configuration is not a leading hypothesis because the request never reaches HTTP middleware or an API endpoint.

## Recommended next investigation

1. Capture .NET `System.Net.Security` EventSource events for the API handshake without changing protocol settings.
2. If those events remain inconclusive, capture only TCP 58443 with an elevated packet trace. Determine the ServerHello-selected TLS version, whether the client certificate flight is sent, and the direction of the final FIN/RST or TLS alert.
3. Only after recording the negotiated version, test TLS 1.2 for identity-bearing Android clients while leaving bootstrap pairing on the modern default. Treat this as a diagnostic first, not a permanent fix.
4. If TLS 1.2 succeeds, document the exact TLS 1.3 failure and decide whether a narrowly scoped compatibility policy is acceptable.
5. If TLS 1.2 also fails, focus on Schannel client-certificate validation and certificate-chain design. Do not introduce an accept-any certificate callback without a reviewed replacement trust model.
6. After testing, remove the temporary certificate from `CurrentUser\TrustedPeople` by its exact thumbprint.

## Acceptance condition

This issue is resolved only when, on the physical Pixel 8a and Windows PC:

- approval is followed by a successful mTLS `/api/v1/info` request;
- Android saves the paired PC;
- subsequent text and file API requests authenticate with the registered client identity;
- revocation still takes effect on every request; and
- no global or accept-any certificate trust bypass is introduced.
