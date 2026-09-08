# Phone Transfer

AndroidスマートフォンとWindows PCの間で、同一LAN内だけでファイル・テキスト・URLを直接渡すアプリです。クラウドや外部中継サーバーを転送経路に含めません。

**現在のmainには、QR/mTLSペアリング、mDNS再発見、Windowsのhandle-safe共有フォルダ、認証済みファイルAPI、Android SAF upload/download、Foreground Service、Windows durable upload recovery、Android process-kill後のdurable upload recoveryまで実装済みです。** このbranchでは既存Protocol v1設計を維持したまま、AndroidからPCへの`plainText` / `url`送信も追加しています。コア機能は大きく揃っていますが、実Windows/Android端末での受入試験が未完了のため、まだMVP完成とは扱いません。

最新状況は [実装状況](docs/implementation-status.md) と [引き継ぎ資料](docs/handoff.md) を確認してください。

## 構成

- `apps/android`: Kotlin / Compose / StateFlow / SAF / Foreground Service。Android 9以上。
- `apps/windows`: .NET 10 / Windows Forms tray / ASP.NET Core Kestrel。
- `packages/protocol`: OpenAPI正本と生成C#・Kotlinモデル。
- `packages/test-fixtures`: 共通通信データ検証。
- `tests/windows`: 単体・実Kestrel TLS・Windows実filesystem adversarial tests。

## 現在実装されている主な機能

- Windowsで120秒有効のQRを表示し、比較番号を確認してAndroidを登録。
- Android Keystoreのclient identityとWindows server SPKI pinを使ったmTLS通信。
- Windows mDNS広告とAndroid NSDによる保存済みPCの再発見。
- WindowsでローカルNTFS/ReFSの受信フォルダを設定。
- retained directory handle基準のWindows filesystem adapterで、junction/symlink/reparse point/hardlinkや差し替え競合をfail closed。
- Androidから共有フォルダを参照し、SAFで選んだファイルを送受信。
- Windows uploadはprivate stagingへ書き込み、flush後にdurable journalのoffsetを進め、SHA-256検証後にsame-volume no-overwrite renameで完成。
- Windows再起動後はSQLite journalとstaging identityをreconcileし、server側のtransfer stateを復旧。
- Android uploadはlocal durable journal、stable idempotency key、SAF capability、source name/size/SHA-256、server transfer ID/offsetを保持し、process kill後にWindows stateをauthorityとして安全にreconcile。
- Android resume前は保存済みPC identity、実際のpersisted URI permission、source full hashを再検証。
- serverが既にCompletedならAndroid sourceを再openせずcompletion receiptへ収束。
- interrupted downloadはgeneric SAF destinationの安全な継続を保証できないため、process kill後の同一destination resumeを意図的に行わない。
- Androidから接続中のPCへ、最大65,536文字のplain textまたはhttp/https URLをmTLSで送信。response loss時は同じidempotency keyで再試行する。
- Windowsは受信した最新text/URLを表示・コピーできる。URLは受信だけでは開かず、PCユーザーが明示的に「URLを開く」を押した場合だけ既定ブラウザーを起動する。

## 開発

.NET 10 SDK、JDK 17、Gradle 8.13、Android SDK 36 / Build Tools 35.0.0、Python 3.12以上を用意します。`ANDROID_HOME`を設定してください。GradleはCIのsetup-gradleで固定し、ローカルも同じ版を使用します。

```sh
python -m pip install -r scripts/requirements.txt
python scripts/validate_protocol.py
python scripts/generate_protocol.py --check
dotnet build PhoneTransfer.slnx
dotnet test tests/windows/PhoneTransfer.Tests
dotnet format PhoneTransfer.slnx --verify-no-changes
gradle -p apps/android spotlessCheck :protocol:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
```

Windows側は選択したLANアダプターのprivate IPv4で待ち受けます（登録用HTTPS: 58442、mTLS API: 58443）。必要な場合はWindowsファイアウォールでプライベートネットワークに限って許可してください。保存済みPCの接続先変更はmDNS候補をそのまま信用せず、既存SPKI pin・client証明書・`/api/v1/info`のDevice ID確認後にだけ保存します。

Windowsでトレイを起動:

```sh
dotnet run --project apps/windows/PhoneTransfer.App
```

## Durable recoveryの境界

WindowsのSQLite transfer journalがupload progressのserver-side authorityです。基本順序は `write → flush → journal commit → response` で、renameがfile commit pointです。DB commitの成否が曖昧な場合やstaging/destination identityを証明できない場合はfail closedにします。

Androidのlocal journalはserver progressのauthorityではありません。process-kill後はWindowsのtransfer statusを再取得し、server offsetがlocalより進んでいれば採用し、serverがlocalより後ろなら不整合として停止します。ユーザーcancel intentはremote cancellationより先にdurable化し、Completedとの競合ではserver Completedを優先します。

詳細は [Windows durable recovery](docs/architecture/durable-transfer-recovery.md) と [Android durable recovery](docs/architecture/android-durable-transfer-recovery.md) を参照してください。

## Text / URLの境界

今回実装する配送方向はAndroid → PCだけです。wire modelは既存Protocol v1の`plainText` / `url`、per-device idempotency keyをそのまま使います。Windows側のuser-visible historyはこのincrementでは持たず、最新受信内容だけを表示します。PC→Android配送用listener/pollingも追加しません。

URLはabsolute `http` / `https` のみをURL kindとして受け付け、資格情報を埋め込んだURLは拒否します。受信時に自動実行・自動openは行いません。

## 残っているもの

MVP判定前に最も重要なのは実機acceptanceです。Android↔Windowsの実端末で、QR/mDNS/SAF、process kill、端末/PC再起動、Wi-Fi断、screen-off、FGS timeout、multi-GB、disk-full、sleep/resume、ReFSや実mounted-volume等を確認する必要があります。text/URLについても実端末からの送信、トレイ通知、copy、明示URL openを確認します。

実装として残る主な後続項目は、Windows journal retention/maintenance、DHCP/Wi-Fi adapter変更時の自動rebind、entry pagination、ACTION_SEND/MULTIPLE、text history / PC→Android delivery、必要ならbounded recovery schedulerです。これらはdurable upload correctnessや今回の片方向text送信とは分離して進めます。

## 設計

[Architecture](docs/architecture/overview.md) · [Protocol](docs/protocol/v1.md) · [Threat model](docs/security/threat-model.md) · [Basic file transfer review](docs/security/basic-file-transfer-review.md) · [Android SAF review](docs/security/android-saf-transfer-review.md) · [ADR](docs/decisions)
