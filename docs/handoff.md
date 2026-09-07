# 開発引き継ぎ — Android QRペアリング接続

更新日: 2026-09-08 JST

PR #1のWindows基盤に続き、PR #2 / `feature/android-pairing` でAndroid側の接続導線を実装しました。mainへのマージは行っていません。

## 今回の追加

- Android Keystoreの非エクスポートP-256鍵。client-auth EKU、digitalSignature、非CAを持つ自己署名証明書を標準署名APIで作成。秘密鍵は保存ファイルに含めません。
- ZXingによるオフラインQR読取と内容貼付。登録署名と双方で一致する6桁の確認番号。
- QRのSPKI固定・証明書期限とserver-auth用途を確認するOkHttp HTTPS。プロキシ、リダイレクト、平文への移行を禁止。
- 署名付き承認状態取得、中止、120秒の待機上限、128 KiBの応答上限。
- PC承認後にmTLS `/api/v1/info` のDevice IDとprotocol versionを確認してからAtomicFileへ保存。QRトークンは永続化しません。
- 保存したPCへの接続確認とローカル削除。削除や中止はPC側の登録を解除しないため、再登録時はPC側の解除が必要です。

## 実機受入手順

1. Windowsトレイを起動し「スマホを登録」でQRを表示。両端末を同じLANに置き、Windowsのプライベートネットワークで58442/58443を許可。
2. Androidで「PCのQRを読み取る」。カメラを許可してPCのQRを読む。
3. 両画面の6桁が一致することを確認し、PC側で承認。Androidで接続完了とPC一覧への追加を確認。
4. Androidを終了・再起動して「接続を確認」。続いてPC側で端末を解除し、Androidの接続確認が失敗することを確認。
5. 未承認で中止、QR期限切れ、PC拒否、Wi-Fi切断、カメラ拒否を確認。PC承認後にスマホ側保存まで到達しなかった場合はPC側の登録を解除してやり直す。
6. Android側の登録削除とPC側解除後に再登録。Keystore鍵の再利用を確認。

Windows GUI、AndroidカメラとKeystore、実LANの相互接続はこの環境では確認していません。CI成功だけでPhase 2完了と判断しないでください。

## 次の作業

Android接続の実機受入後、ADR002に沿ってWindowsのDNS-SD広告とAndroid NsdManagerを実装。mDNSは未実装です。保存済みPCのIPが変わった場合は現時点では双方の登録を解除し、新しいQRで再登録します。mDNS導入時は広告を候補としてのみ扱い、保存済みDevice IDとSPKI pinを維持します。

その後Phase 3の共有設定と安全なファイルI/Oへ進みます。ファイル・テキスト転送は未実装です。

---

以下はPR #1時点の記録です。上記の追加範囲を優先してください。

# 開発引き継ぎ — Windowsペアリング基盤の区切り

更新日: 2026-09-07 UTC / 2026-09-08 JST

## この時点の完成範囲

**mainへの取り込みは開発途中のチェックポイントです。ファイルやテキストを転送できるMVPの完成・製品リリースを意味しません。**

WindowsのトレイからHTTPSサーバーを起動し、QR表示、確認番号による承認、登録端末一覧、登録解除まで操作する実装があります。実KestrelとWindowsのCNG鍵を使った統合テストがあります。Androidは画面の土台とQR入力の検証部品までで、スマホとPCをつないだ一連の利用はまだできません。

元の実装はPR #1の `feature/lan-transfer-mvp`。次の作業は最新mainから新しい作業ブランチを切ってください。mainへの直接実装は行いません。

## フェーズと残り

| Phase | 状態 | 次に必要なこと |
|---|---|---|
| 1: 開発基盤 | 完了 | モノレポ、CI、レイヤ分離、OpenAPI、モデル生成を維持 |
| 2: 発見・ペアリング・mTLS | Windows側の基盤実装済み | Android Keystore、固定公開鍵HTTPS、QRカメラ、接続/保存/解除UI、双方のmDNS、実機接続 |
| 3: ファイル操作 | 未実装 | 共有設定、安全なWindowsファイルアクセス、一覧、ストリーミング送受信、Android SAF |
| 4: 大容量・復旧 | 設計と一部ドメイン部品のみ | チャンク永続化、再開、ハッシュ、キャンセル、孤児清掃、競合/異常終了試験 |
| 5: テキスト・履歴 | プロトコル設計のみ | plainText/URL、保存OFF、保持制限、履歴UI、PCでの明示操作 |
| 6: UX・監査 | 未完了 | 実機試験、侵入/競合試験、バックグラウンド制約、エラー表示、導線整理 |

アップロード/ダウンロード/テキスト等のOpenAPI定義は将来の契約です。APIが実装済みだと解釈しないでください。現在実装されているのは `/api/v1/info` と、登録要求/署名付きステータス取得の `/pairing/v1/requests` です。

## まず読むもの

1. [実装状況](implementation-status.md)
2. [全体構成](architecture/overview.md)
3. [プロトコル](protocol/v1.md) と正本 `packages/protocol/openapi.json`
4. [Threat model](security/threat-model.md)
5. [ADR一覧](decisions): 特に002 discovery、003 security、008 filesystem、009 Android lifetime、010 pairing proof、012 admission/device store

## コードの入口

| 責務 | 主な場所 |
|---|---|
| トレイ・画面 | `apps/windows/PhoneTransfer.App/` の `Program`、`TrayApplicationContext`、`ServerWindow`、`PairingDialog` |
| Windows起動・終了の組み立て | `apps/windows/PhoneTransfer.Host/WindowsServerRuntime.cs` |
| HTTPS/mTLSホスト | `PhoneTransfer.Host/PairingHost.cs`、`ServerHost.cs` |
| APIと統一エラー | `apps/windows/PhoneTransfer.Api/` |
| 一時トークン・署名・ローカル承認 | `PhoneTransfer.Application/Pairing/` |
| 永続端末・失効 | `PhoneTransfer.Infrastructure/Persistence/SqliteDeviceRegistry.cs` |
| Windows秘密鍵 | `PhoneTransfer.Infrastructure/Security/WindowsServerCertificate.cs` |
| ローカルLANアダプター選択 | `PhoneTransfer.Infrastructure/Discovery/LanAdapters.cs`。これはmDNSではない |
| Android QR入力検証 | `apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/security/PairingInvitation.kt` |
| Android画面の土台 | `apps/android/app/src/main/kotlin/com/shinpstudio/phonetransfer/ui/` |
| Windows試験 | `tests/windows/PhoneTransfer.Tests/` |
| プロトコル生成 | `scripts/generate_protocol.py`、`packages/protocol/`。生成コードを直接編集しない |

## Windowsの起動とデータ

READMEの開発環境を準備し、Windowsで次を実行します。

```sh
dotnet run --project apps/windows/PhoneTransfer.App
```

- 選択したWi-Fi/有線LANのプライベートIPv4に限定して待ち受けます。IPv6対応と自動再発見は未実装です。
- 登録用HTTPSは58442、mTLS APIは58443。同一のサーバー鍵を使用します。
- Windowsファイアウォールで必要な場合はプライベートネットワークに限定して許可します。自動的なルール追加は実装していません。
- 「スマホを登録」で120秒のQRを表示し、相手端末名と両画面の確認番号を確認して承認します。Android側の操作画面は未実装です。
- ウィンドウを閉じるとトレイに残ります。「終了」で両ホストを停止します。同一セッションでの二重起動を防ぎます。
- 永続設定は `%LOCALAPPDATA%\PhoneTransfer`。`device-id` と `devices.db` を使用します。SQLiteはWALです。
- 秘密鍵はCurrentUserのCNG、証明書はCurrentUser/My。PFXや通常ファイルに秘密鍵を書き出しません。
- ペアリング復旧目的で `device-id` やDBを安易に削除しないでください。相手の登録情報と不整合になります。解除/再ペアリングを基本にします。

## セキュリティ上の不変条件

- QRは短時間・一回限り・メモリ内のチャレンジです。所持だけでは登録されず、PC画面での承認が必要です。承認/失効のHTTP APIはありません。
- 登録要求はP-256/SHA-256署名で端末ID、表示名、トークン、証明書ハッシュを結びます。署名形式・確認番号の計算を独自に変更せず、プロトコル文書に合わせます。
- QRを閉じると未承認要求は拒否し、承認済みの結果は元の期限まで取得できるよう保持します。新しいQR発行は古いセッションを置き換えます。
- mTLSハンドシェイクだけでなくリクエストごとにも期限と失効を確認します。接続プールの再利用で失効確認を省略しません。
- 既存の有効なDevice IDの鍵を黙って上書きしません。PCで失効後に再登録します。IPやPC表示名を端末IDにしません。
- Windows鍵は非エクスポート。Android鍵もKeystoreを使い、SharedPreferencesやファイルに秘密鍵を書きません。
- QR、トークン、テキスト本文、秘密鍵をログへ出しません。Androidの検証済みQRオブジェクトは `toString()` で内容を伏せます。生の生成DTOにも秘密情報があるためログ出力しません。
- Android QR検証は数値のプライベート/link-local IPv4、HTTPS、同一ホストの別ポートのみ許可します。ホスト名、外部IP、平文、ユーザー情報、パス/クエリ/fragmentを拒否します。これだけでTLS pinningを実装したことにはなりません。
- パス構文の単体検証はありますが、junction/symlink差し替えを防ぐ安全なファイルI/Oは未実装です。`Path.Combine`だけで共有を公開しないでください。

## 再開する順序

1. Android KeystoreのP-256鍵、client-auth EKU付き証明書、登録署名、署名付き結果取得を実装。秘密鍵を取り出さず標準署名APIを使用します。
2. OkHttpのTLS検証を実装。QRのSPKIに固定し、有効期限・server-auth用途も確認します。リダイレクト・プロキシ・平文fallbackでトークンや端末認証が他所へ流れない設計にします。
3. QR読取→確認番号→承認待ち→mTLSのinfo確認→保存までRepository/ViewModel/StateFlowで接続。UIから直接HTTPしません。保存失敗/キャンセル/再ペアリングを扱い、Androidから保存したPCの削除も用意します。
4. ADR002に沿ってmDNSをWindows/Androidに実装。広告は接続先候補として扱い、認証根拠にしません。DHCP変更後もDevice IDとpinを維持します。
5. 実WindowsとAndroidでPhase 2を受け入れ、その後Phase 3へ進みます。共有IDと相対パス、書込不可共有、上書き禁止、handle安全性を先に固めます。
6. 数GB転送はストリーミング。4 MiB上限のチャンク、確定済みoffset、サーバー側SHA-256、非公開の一時領域、検証後の同一ボリューム内移動、キャンセルとの直列化を実装します。
7. Android SAF、共有Intent、Foreground Service、復旧・清掃、履歴の保持/保存OFFを追加し、Phaseごとの完了ゲートを通します。

## 検証と完了ゲート

```sh
python -m pip install -r scripts/requirements.txt
python scripts/validate_protocol.py
python scripts/generate_protocol.py --check
dotnet build PhoneTransfer.slnx --configuration Release
dotnet test tests/windows/PhoneTransfer.Tests --configuration Release
dotnet format PhoneTransfer.slnx --verify-no-changes
gradle -p apps/android spotlessCheck :protocol:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
git diff --check
```

各Phaseでbuild/test/lint・整形/diff・ドキュメント更新を揃えます。チェックを無効化して通しません。Androidの依存更新通知3種だけを参考情報にしている理由はADR011にあります。

Windowsの57件は `052056b` で成功しています（[CI](https://github.com/shinp-dev/phone-transfer/actions/runs/34164986068)）。実HTTPS登録、承認、mTLS、既存接続の失効、SQLite再読込、CNG非エクスポート、再起動後の接続を含みます。Androidには既存のoffset試験3件とQR試験4件があります。最終コミットの検証結果は[PR #1](https://github.com/shinp-dev/phone-transfer/pull/1)と[Actions](https://github.com/shinp-dev/phone-transfer/actions)を確認してください。

この作業環境ではWindows GUIとAndroid実機の操作確認はしていません。CIのWindows試験は実TLS/CNGを使いますが、カメラ、mDNS、Windowsファイアウォール、実機Keystore、Wi-Fi切断、画面OFF、プロセスkill、PCスリープ、4 GiB超、SAF provider差は別途必要です。

## 次の担当者向けの短い指示

> docs/handoff.md と docs/implementation-status.md を読み、最新mainから作業ブランチを作る。現在はPhase 2途中で、WindowsトレイのQR承認とmTLS基盤は実装済み。AndroidのKeystore・pinning・QR読取・保存済み接続とmDNSを完成させる。ファイル転送は未実装。main取り込みをMVP完成と扱わず、既存テストと安全上の不変条件を維持してPhase単位で実装・検証・PRを進める。
