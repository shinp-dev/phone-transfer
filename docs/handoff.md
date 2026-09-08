# 開発引き継ぎ — Phase 3 転送API着手前

更新日: 2026-09-08 JST

この文書は現在のmainを前提にした引き継ぎ正本です。古いPRごとの手順より、[実装状況](implementation-status.md)、[全体構成](architecture/overview.md)、[Protocol v1](protocol/v1.md)、[Threat model](security/threat-model.md)、ADRを優先してください。

## 現在の到達点

PR #1〜#7までmainへ取り込み済みです。現在の製品はまだ転送可能なMVPではありませんが、次の基盤は実装済みです。

- Windowsトレイ、選択LANアダプター上のHTTPS bootstrap 58442 / mTLS API 58443。
- 120秒・single-use QR、P-256署名proof、6桁比較コード、PCローカル承認、SQLite登録/失効。
- Android Keystore非エクスポート鍵、SPKI pin、client証明書、署名付きpairing status、mTLS `/api/v1/info`確認後のAtomicFile保存。
- Windows native DNS-SD `_phone-transfer._tcp` とAndroid `NsdManager`。mDNSはrouting候補だけで、保存済みSPKI pin・client identity・Device ID確認後にだけendpointを更新。
- Windows受信フォルダ設定。ローカルNTFS/ReFSのみ。UNC、volume root、アプリデータ領域、reparse ancestryを拒否。
- Windows handle-safe filesystem adapter。保持済み親directory handleから1 componentずつopenし、reparse point、junction、symlink、hardlink、rename/delete差し替えをfail-closedに扱う。
- private ACL staging、安定read、bounded listing、same-volume no-overwrite completionの内部primitive。

**重要:** `/api/v1/shares`、file listing、upload、download、text/history等のOpenAPI routeはまだ設計契約です。実装済みと解釈しないでください。現在ネットワークで提供するmain APIは `/api/v1/info` のみで、pairing bootstrapは `/pairing/v1/requests` 系だけです。

## まず守る不変条件

1. Windowsが唯一のlistener。Androidはdiscovery/pairing/APIを開始する。クラウド・relayを入れない。
2. mDNS/IP/PC名は認証根拠にしない。端末identityはDevice ID、証明書、SPKI pinで束縛する。
3. mTLS handshakeだけで認可完了としない。各requestで現在の登録/失効を確認する。
4. `RelativeSharePath`は構文防御でありfilesystem containmentではない。ファイルアクセスは必ず`IShareFileSystem`経由にする。`Path.Combine`や`GetFullPath` prefix比較を安全境界にしない。
5. Windows handle-safe adapterのnative handle/OS path/FileStreamをApplication/API/UIへ漏らさない。
6. stagingはprivate capabilityとして扱い、通常のshare listingから見せない。既存destinationを暗黙overwriteしない。
7. OpenAPIがwire SSOT。生成C#/Kotlinモデルを直接編集しない。
8. Android Activity/ViewModelに長時間転送の寿命を持たせない。将来の実転送はuser-started foreground service側へ寄せる。

## PR #7 filesystemの要点

`apps/windows/PhoneTransfer.Application/Files/IShareFileSystem.cs` がApplication契約、`PhoneTransfer.Infrastructure/Storage/WindowsShareFileSystem.cs` と `WindowsFileNative.cs` がWindows実装です。

- `CreateFileW`でlocal volumeをanchor。
- `NtCreateFile`の`RootDirectory`で子componentを相対open。
- `OBJ_DONT_REPARSE`、`FILE_OPEN_REPARSE_POINT`、handle inspectionでreparse処理を拒否。
- root ancestryとoperation ancestryをdelete sharingなしで保持。
- `GetFinalPathNameByHandleW`と`FileIdInfo`は追加assertionとして利用。
- 複数hardlink、pending deleteを拒否。
- completionはstaging open handleから`NtSetInformationFile(FileRenameInformation)`、`ReplaceIfExists = FALSE`。
- private stagingは作成時からcurrent-user-only protected DACL。

Windows CIではjunction、symlink、hardlink、root差し替え、in-place reparse mutation、open/use差し替え、destination race、ACL、8.3 alias、handle cleanupなどのadversarial testを含め109 testsがskipなしで成功しました。実ReFS volumeと実volume mount provisioningは別Windows環境での受入が必要です。

## 次の作業順

### 1. 小さいpre-transfer cleanup

- Android Bouncy Castleを既知修正版へ更新し回帰test。
- Saved-PC JSON読み込みに明示サイズ上限を設ける。
- README / handoff / implementation-statusを現在地へ同期。
- OpenAPIのserver example portを実装の58443へ揃える。

このcleanupでは転送routeを有効にしません。

### 2. Basic file list/upload/download

新しいfeature branchで、まずresumeなしの最小転送を実装します。

- `IShareFileSystem`だけをfilesystem入口にする。
- share listingとentry listingはpermission、limit、typed errorをAPI層で適用。
- uploadはbounded streamingでprivate stagingへ書き、SHA-256検証後no-overwrite completion。
- downloadはstable open handleからstreamし、strong SHA-256 ETagとsingle rangeを扱う。
- physical path、exception、secretをresponseへ出さない。
- 1 request / 1 transferが共有root外へ抜けるfallbackを作らない。

最初から完全resume state machineを同じPRへ入れないでください。

### 3. Android SAF

basic server APIが固まった後、Android側をPairingRepositoryへ詰め込まず、transfer repository/serviceを分離して実装します。

- ACTION_OPEN_DOCUMENT / CREATE_DOCUMENT等のSAF URIを扱う。
- content URIをpathへ変換しない。
- nonseekable providerを考慮する。
- long-running transferはforeground service所有。
- download完成判定はdigest確認後。SAF export失敗を完成扱いにしない。

### 4. Durable resume/recovery

ここは難所なので別PR。durable DB、committed offset、chunk rollback/truncate、startup reconciliation、crash between rename/DB commit、disk-full、cancel/revoke競合を状態機械として実装します。PR #7のstaging primitiveに必要ならhandle-safe truncate capabilityを追加します。

## 実機受入として残るもの

- Windows 11 private firewall、QR pairing、Android camera/Keystore、native DNS-SD/NSD。
- DHCP/Wi-Fi変更後のrediscovery。Windows側の自動rebindはまだ未実装で、現時点では「接続をやり直す」で再選択する。
- multicast-isolated LANでQR fallback。
- multi-NICで選択adapterだけを使用すること。
- sleep/resume。
- ReFS、実volume mount point環境でのfilesystem fail-closed確認。
- SAF seekable/nonseekable、4GiB+、screen-off/process kill/FGS制約。
- disk-full/crash/recoveryはresume実装後。

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

常にlatest mainから作業branchを切り、CI成功を確認してPRにしてください。mainへ直接実装しません。セキュリティ境界、transfer state machine、filesystem containmentを変更するPRは独立監査してからmergeします。
