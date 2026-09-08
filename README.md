# Phone Transfer

AndroidスマートフォンとWindows PCの間で、同一LAN内だけでファイル・テキストを直接転送するアプリです。

**現在は接続・認証・共有フォルダ・Windows安全ファイルI/O基盤まで実装済みですが、まだ転送可能なMVPではありません。** WindowsトレイからQR表示・端末承認・登録解除・受信フォルダ設定、AndroidからQR読取・確認番号表示・mTLS接続確認・PC登録保存/削除、mDNSによる保存済みPCの再発見ができます。Windows側にはhandle-safeな内部filesystem adapterがありますが、`/api/v1/shares`、upload、download、text等の転送APIはまだ有効化していません。[実装状況](docs/implementation-status.md)と[引き継ぎ資料](docs/handoff.md)を正本として確認してください。

## 構成

- `apps/android`: Kotlin / Compose / StateFlow。Android 9以上。
- `apps/windows`: .NET 10 / Windows Forms tray / ASP.NET Core Kestrel。
- `packages/protocol`: OpenAPI正本と生成C#・Kotlinモデル。
- `packages/test-fixtures`: 共通通信データ検証。
- `tests/windows`: 単体・実Kestrel TLS・Windows実filesystem adversarial tests。

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

Windows側は選択したLANアダプターのprivate IPv4で待ち受けます（登録用HTTPS: 58442、mTLS API: 58443）。必要な場合はWindowsファイアウォールでプライベートネットワークに限って許可してください。登録ボタンで120秒間有効なQRを表示し、スマホとの確認番号が一致した場合だけPC側で承認します。保存済みPCの接続先変更はmDNS候補をそのまま信用せず、既存SPKI pin・client証明書・`/api/v1/info`のDevice ID確認後にだけ保存します。

Windowsでトレイを起動: `dotnet run --project apps/windows/PhoneTransfer.App`。

受信フォルダはローカルNTFS/ReFSに限定されます。Windowsの内部filesystem adapterは保持済みdirectory handle基準で1 componentずつ辿り、junction/symlink/reparse pointや差し替え競合をfail-closedに扱います。private stagingとno-overwrite completionも内部実装済みです。ただし、このadapterはまだHTTP経路へ公開していません。

## 設計

[Architecture](docs/architecture/overview.md) · [Protocol](docs/protocol/v1.md) · [Threat model](docs/security/threat-model.md) · [ADR](docs/decisions)

外部サービスはビルド時の依存取得とGitHub CIにのみ使用します。製品の通信経路にはクラウド・中継サーバーを含めません。MVP完成には実Windows・Android端末でのQR/mDNS受入、基本list/upload/download、Android SAF、その後のresume/recovery実装と実機検証が必要です。
