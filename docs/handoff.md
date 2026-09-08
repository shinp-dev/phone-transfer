# 開発引き継ぎ — Phase 3 Basic file transfer

更新日: 2026-09-08 JST

この文書は現在のmain＋`feature/basic-file-transfer`を前提にした引き継ぎ正本です。古いPRごとの説明より、[実装状況](implementation-status.md)、[全体構成](architecture/overview.md)、[Protocol v1](protocol/v1.md)、[Threat model](security/threat-model.md)、ADRを優先してください。

## 現在の到達点

PR #1〜#8はmainへ取り込み済みです。接続・認証・mDNS・共有フォルダ設定・Windows handle-safe filesystemがmainにあり、`feature/basic-file-transfer`ではそのfilesystem adapterだけを使う最初の認証済みfile APIを実装中です。

mainで既に実装済み:

- Windowsトレイ、選択LANアダプター上のHTTPS bootstrap 58442 / mTLS API 58443。
- single-use QR、P-256 proof、比較コード、PCローカル承認、SQLite登録/失効。
- Android Keystore、SPKI pin、client証明書、mTLS `/api/v1/info`確認後のAtomicFile保存。
- Windows DNS-SD `_phone-transfer._tcp` とAndroid `NsdManager`。mDNSはrouting候補のみ。
- Windows受信フォルダ設定。ローカルNTFS/ReFSのみでUNC、volume root、アプリデータ領域、reparse ancestryを拒否。
- Windows handle-safe filesystem adapter。保持済み親directory handleから1 componentずつopenし、reparse point、junction、symlink、hardlink、rename/delete差し替えをfail closedに扱う。
- private ACL staging、stable read、bounded listing、same-volume no-overwrite completion。

`feature/basic-file-transfer`で追加中:

- authenticated share list / entry list;
- process-local transfer create/status/cancel;
- 4 MiB以下のupload chunk、exact committed offset、private stagingへのwrite+flush;
- SHA-256 verify後のno-overwrite completion;
- stable handle download、strong SHA-256 ETag、single `Range: bytes=n-` + `If-Match`;
- per-device transfer ownershipとBrowse/Upload/Download permission enforcement;
- per-device active-transfer capとtotal staging quota;
- 実Kestrel＋実Windows filesystem integration test。

**重要:** このincrementはdurable resume/recoveryではありません。transfer registryはprocess-localです。PC再起動、DB永続化、startup reconciliation、partial-chunk truncate、rename/DB crash reconciliation、disk-full recoveryは別PRです。Android SAFもまだ未実装です。

## 必ず守る不変条件

1. Windowsが唯一のlistener。Androidはdiscovery/pairing/APIを開始する。クラウド・relayを入れない。
2. mDNS/IP/PC名は認証根拠にしない。identityはDevice ID、client証明書、server SPKI pinで束縛する。
3. mTLS handshakeだけで認可完了としない。各HTTP requestで現在の登録/失効を再確認する。
4. `RelativeSharePath`は構文防御でありfilesystem containmentではない。ネットワーク由来のfile accessは必ず`IShareFileSystem`経由にする。`Path.Combine`、`GetFullPath`、string prefix比較をsecurity boundaryにしない。
5. Windows native handle、physical OS path、FileStreamをApplication/API/UIへ漏らさない。
6. stagingはprivate capabilityとして扱い通常listingに出さない。既存destinationを暗黙overwriteしない。
7. OpenAPIがwire SSOT。生成C#/Kotlinモデルを直接編集しない。
8. Androidの将来の長時間転送をActivity/ViewModelに所有させない。transfer repository/serviceとforeground serviceへ分離する。

## Basic uploadの現在の意味

`POST /api/v1/transfers`でmetadataを登録し、`PATCH .../content`で1 request最大4 MiBを送ります。APIは1 request chunkだけをbounded bufferへ読み切ってからstaging handleへwriteし、`Flush`成功後にだけcommitted offsetを進めます。これにより、このbasic incrementではdisconnect途中のrequestをdurable commit扱いせずに済みます。

これは将来のresume state machineの代替ではありません。大容量・再起動resumeではprivate stagingを永続transfer recordと結び、committed boundaryへのtruncate/reconcileを別途設計してください。

completionはstagingを独立SHA-256検証し、既存handle-safe `CompleteNoReplace()`をauthorityとして使用します。事前のdestination存在確認はadvisoryであり、race時のoverwrite防止はkernel renameのno-replace semanticsが担います。

## Basic downloadの現在の意味

fileはhandle-safe adapterからstable handleとしてopenし、その同じhandleからSHA-256 ETagを計算してstreamします。resume形式はsingle open-ended rangeのみです。`Range: bytes=n-`を使う場合は同じstrong ETagの`If-Match`を必須にし、違えば412とします。複数rangeやsuffix rangeはこのincrementでは対応しません。

## 次の作業順

### 1. Basic file APIのCI・独立監査

- Windows format / Release build / testsをgreenにする。
- mTLS request-by-request authorization、permission、transfer ownershipを確認。
- share root physical pathがresponseに出ないことを確認。
- file pathが必ず`IShareFileSystem`へ入り、API/Hostで再構成されていないことを確認。
- upload offset、hash mismatch、destination conflict、cancel、download ETag/rangeの異常系を追加確認。

### 2. Android SAF / transfer ownership

server APIが固まった後に別branchで実装します。

- `PairingRepository`へtransfer責務を追加しない。
- ACTION_OPEN_DOCUMENT / CREATE_DOCUMENT等のcontent URIを扱い、URIをfilesystem pathへ変換しない。
- seekable/nonseekable providerを考慮する。
- long-running transferはforeground service所有。
- downloadはdigest確認後に完成扱いし、SAF export失敗をcompletedにしない。

### 3. Durable resume/recovery

難所なので別PR。persistent transfer DB、committed offset、handle-safe truncate、startup reconciliation、crash between rename/DB commit、disk-full、cancel/revoke raceを状態機械として実装します。

## 実機受入として残るもの

- Windows 11 private firewall、QR pairing、Android camera/Keystore、native DNS-SD/NSD。
- DHCP/Wi-Fi変更後のrediscovery。Windows側の自動rebindは未実装で、現時点では「接続をやり直す」で再選択する。
- multicast-isolated LANでQR fallback、multi-NIC、sleep/resume。
- ReFS、実volume mount point環境でのfilesystem fail-closed確認。
- basic upload/downloadの実Android↔Windows streaming、大容量・disk-full。
- SAF seekable/nonseekable、4 GiB+、screen-off/process kill/FGS制約。
- crash/recoveryはdurable resume実装後。

## 開発ゲート

Windows変更:

```sh
dotnet format PhoneTransfer.slnx --verify-no-changes
dotnet build PhoneTransfer.slnx --configuration Release
dotnet test tests/windows/PhoneTransfer.Tests --configuration Release
git diff --check
```

Android変更:

```sh
gradle -p apps/android spotlessCheck :protocol:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
```

Protocol変更:

```sh
python scripts/validate_protocol.py
python scripts/generate_protocol.py --check
git diff --check
```

常にlatest mainから作業branchを切り、CI成功を確認してPRにしてください。mainへ直接実装しません。filesystem containment、auth、durable transfer state machineを変更するPRは独立監査してからmergeします。
