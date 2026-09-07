# Phone Transfer

AndroidスマートフォンとWindows PCの間で、同一LAN内だけでファイル・テキストを直接転送するアプリ。

**現在は開発基盤とペアリング認証部品の実装段階です。転送可能なMVPではありません。** WindowsトレイからQR表示・端末承認・登録解除が可能です。Androidのペアリング画面とファイル転送は開発中です。[実装状況](docs/implementation-status.md)で利用可能範囲を確認してください。

## 構成

- `apps/android`: Kotlin / Compose / StateFlow。Android 9以上。
- `apps/windows`: .NET 10 / Windows Forms tray / ASP.NET Core Kestrel。
- `packages/protocol`: OpenAPI正本と生成C#・Kotlinモデル。
- `packages/test-fixtures`: 共通通信データ検証。
- `tests/windows`: 単体テストと実Kestrel TLSテスト。

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

Windows側は選択したLANアダプターのIPv4で待ち受けます（登録用HTTPS: 58442、mTLS API: 58443）。必要な場合はWindowsファイアウォールでプライベートネットワークに限って許可してください。登録ボタンで120秒間有効なQRを表示し、スマホとの確認番号が一致した場合だけ承認します。現在、APIは接続情報のみ提供します。

Windowsでトレイを起動: `dotnet run --project apps/windows/PhoneTransfer.App`。

## 設計

[Architecture](docs/architecture/overview.md) · [Protocol](docs/protocol/v1.md) · [Threat model](docs/security/threat-model.md) · [ADR](docs/decisions)

外部サービスはビルド時の依存取得とGitHub CIにのみ使用します。製品の通信経路にはクラウド・中継サーバーを含めません。MVPの完成には実Windows・Android端末での検証が必要です。
