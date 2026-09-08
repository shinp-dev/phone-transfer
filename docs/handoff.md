# 開発引き継ぎ — Phase 3 Android SAF transfer

更新日: 2026-09-08 JST

この文書は現在のmain＋`feature/android-saf-transfer`を前提にした引き継ぎ正本です。古いPRごとの説明より、[実装状況](implementation-status.md)、[全体構成](architecture/overview.md)、[Protocol v1](protocol/v1.md)、[Threat model](security/threat-model.md)、ADRを優先してください。

## 現在の到達点

PR #1〜#9はmainへ取り込み済みです。接続・認証・mDNS・共有フォルダ設定・Windows handle-safe filesystem・認証済みbasic file APIがmainにあります。`feature/android-saf-transfer`では、そのAPIをAndroidのSAF content URIとdataSync Foreground Serviceへ接続しています。

mainで既に実装済み:

- Windowsトレイ、選択LANアダプター上のHTTPS bootstrap 58442 / mTLS API 58443。
- single-use QR、P-256 proof、比較コード、PCローカル承認、SQLite登録/失効。
- Android Keystore、SPKI pin、client証明書、mTLS `/api/v1/info`確認後のAtomicFile保存。
- Windows DNS-SD `_phone-transfer._tcp` とAndroid `NsdManager`。mDNSはrouting候補のみ。
- Windows受信フォルダ設定。ローカルNTFS/ReFSのみでUNC、volume root、アプリデータ領域、reparse ancestryを拒否。
- Windows handle-safe filesystem adapter。保持済み親directory handleから1 componentずつopenし、reparse point、junction、symlink、hardlink、rename/delete差し替えをfail closedに扱う。
- private ACL staging、stable read、bounded listing、same-volume no-overwrite completion。
- authenticated share/list、process-local basic upload、stable handle download、strong SHA-256 ETag、per-device permissions/ownership、revoke cleanup、request/transfer bounds。

`feature/android-saf-transfer`で追加:

- `PairingRepository`とは独立した`FileTransferRepository`。
- 保存済みPCのSPKI pin＋Android Keystore client証明書を使うfile API専用mTLS接続。
- `ACTION_OPEN_DOCUMENT` upload source、`CREATE_DOCUMENT` download destination。content URIをfilesystem pathへ変換しない。
- Windowsのrelative-path wire ruleと同じ危険名を送信前に拒否する`RemotePathRules`。
- upload sourceを1回streamしてsize/SHA-256を確定し、同じURIを再openして1 MiB chunk＋exact offsetで送信。
- PATCH/completeのresponseがnetwork failureで失われた場合、同じprocess-local transfer IDをGETしてcommit済みoffset/completed状態を確認してから継続判断。
- downloadはfull streamをSAF outputへ書きながらSHA-256を計算し、Windowsのstrong ETagとdeclared lengthが一致した場合だけsuccess。
- 非exportの`dataSync` Foreground Serviceが長時間transferを所有。Activity/ViewModelはservice開始・UI状態観測・cancelだけを担当。
- user cancel、dataSync timeout、service failureの状態をprocess-local StateFlowでUIへ反映。

**重要:** これはdurable resume/recoveryではありません。Windows transfer registryもAndroid transfer-status busもprocess-localです。PC/Android process kill、persistent offset、startup reconciliation、partial-chunk truncate、rename/DB crash reconciliation、disk-full recoveryは別PRです。

## 必ず守る不変条件

1. Windowsが唯一のlistener。Androidはdiscovery/pairing/APIを開始する。クラウド・relayを入れない。
2. mDNS/IP/PC名は認証根拠にしない。identityはDevice ID、client証明書、server SPKI pinで束縛する。
3. mTLS handshakeだけで認可完了としない。各HTTP requestで現在の登録/失効を再確認する。
4. `RelativeSharePath`/`RemotePathRules`は構文防御でありfilesystem containmentではない。ネットワーク由来のWindows file accessは必ず`IShareFileSystem`経由にする。
5. Windows native handle、physical OS path、Android SAF URIを相互のOS pathへ変換しない。SAF URIはAndroid側のcapabilityとして`ContentResolver`だけへ渡す。
6. stagingはprivate capabilityとして扱い通常listingに出さない。既存destinationを暗黙overwriteしない。
7. OpenAPIがwire SSOT。生成C#/Kotlinモデルを直接編集しない。
8. Androidの長時間転送をActivity/ViewModelに所有させない。Foreground Serviceとtransfer repositoryへ分離する。
9. upload/downloadはtransport successだけで完了扱いしない。uploadはserver SHA-256/no-replace completion、downloadはlength＋strong SHA-256 ETag検証をauthorityにする。

## Android uploadの現在の意味

v1 `CreateTransfer`はtotal sizeとSHA-256を転送開始前に要求します。SAF providerにseekを要求しないため、Androidはsource URIを1回読み切ってhash/sizeを計算し、そのURIを再openして送信します。したがってnonseekable streamは扱えますが、providerが同じdocumentを2回openできない場合はこのbasic v1 clientでは失敗します。大容量sourceをアプリprivate tempへ丸ごとcopyするfallbackは入れていません。

送信は1 MiB chunkです。server limitは4 MiBなので常に範囲内です。各PATCH後のserver committed offsetを確認します。response喪失時は同一transfer IDをGETし、期待offsetまでcommit済みなら同じchunkを二重送信せず先へ進みます。complete response喪失時もserver statusが`completed`なら成功と扱います。

## Android downloadの現在の意味

full downloadのみを使用します。Windowsがstable handleから返すContent-Lengthとstrong SHA-256 ETagを取得し、SAF destinationへstreamしながら同じdigestを再計算します。length/hashの両方が一致するまでUIへsuccessを出しません。

SAF providerによっては途中失敗したoutputを原子的に削除・truncateできません。失敗時は同じURIを`w`で再openしてbest-effort truncateしますが、providerが拒否した場合はpartial destinationが残り得ます。その場合もsuccessとは表示しません。原子的な端末側completionはprovider capabilityに依存するため実機受入対象です。

## Foreground Service ownership

transferは非exportの`dataSync` Foreground Serviceが所有します。manifestに`FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_DATA_SYNC`、`POST_NOTIFICATIONS`を宣言します。user actionでforeground Activityから開始し、進捗notificationとcancel actionを提供します。API 35+のdataSync timeoutではjobをcancelしてserviceを停止します。

一方、Android process自体がkillされた後の再開はまだありません。process kill後にWindows側private stagingが残る可能性はdurable startup reconciliationで解消してください。

## 次の作業順

### 1. Android SAF/FGS CI・独立監査

- Spotless、protocol tests、Android unit tests、lintDebug、assembleDebugをgreenにする。
- Android 9〜最新でmanifest/service declarationが妥当か確認。
- content URIをpathへ変換していないこと、saved pin/client identityをfile APIでも必ず使うことを確認。
- malformed file name、source mutation、ambiguous PATCH/complete response、download length/hash mismatch、cancel/timeoutを確認。

### 2. Real-device basic transfer acceptance

- Pixel等の実機→Windows 11 NTFSでsmall/large upload/download。
- local DocumentsUI、Google Drive等、seekable/nonseekableかつ再open可能なprovider。
- screen off/background、notification denied/allowed、Wi-Fi disconnect、server revoke、PC share変更。
- failed download destinationのprovider別挙動。

### 3. Durable resume/recovery

難所なので別PR。persistent transfer DB、committed offset、handle-safe truncate、startup reconciliation、crash between rename/DB commit、disk-full、cancel/revoke/process-kill raceを状態機械として実装します。

### 4. UX追加

ACTION_SEND/MULTIPLE、複数ファイルqueue、text/historyはdurable基盤と分離して追加します。

## 実機受入として残るもの

- Windows 11 private firewall、QR pairing、Android camera/Keystore、native DNS-SD/NSD。
- DHCP/Wi-Fi変更後のrediscovery。Windows側の自動rebindは未実装で、現時点では「接続をやり直す」で再選択する。
- multicast-isolated LANでQR fallback、multi-NIC、sleep/resume。
- ReFS、実volume mount point環境でのfilesystem fail-closed確認。
- basic upload/downloadの実Android↔Windows streaming、大容量・disk-full。
- SAF provider差、4 GiB+、screen-off、notification permission、FGS timeout、process kill。
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
