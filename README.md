# Phone Transfer

AndroidスマートフォンとWindows PCの間で、同一LAN内だけでファイル・テキストを直接転送するアプリ。

**現在はPhase 1の開発基盤です。転送可能なMVPではありません。** PCのトレイは未設定状態で起動し、転送サーバーは公開しません。Android画面も機能準備中です。[実装状況](docs/implementation-status.md)で利用可能範囲を確認してください。

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

Windowsでトレイを起動: `dotnet run --project apps/windows/PhoneTransfer.App`。

## 設計

[Architecture](docs/architecture/overview.md) · [Protocol](docs/protocol/v1.md) · [Threat model](docs/security/threat-model.md) · [ADR](docs/decisions)

外部サービスはビルド時の依存取得とGitHub CIにのみ使用します。製品の通信経路にはクラウド・中継サーバーを含めません。MVPの完成には実Windows・Android端末での検証が必要です。
