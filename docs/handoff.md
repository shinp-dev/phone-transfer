# 開発引き継ぎ — durable recovery + Android→PC text/URL

更新日: 2026-09-09

## 現在地

- リポジトリ: `shinp-dev/phone-transfer`
- PR #11: Windows durable transfer recovery — merged
- PR #12: Android process-kill durable upload recovery — merged
- PR #13: durable recovery merge後のREADME/status/handoff現行化 — merged
- PR #14: Android → PC text/URL — 実装・CI調整中
- コアfile-transferは大きく揃っているが、physical-device acceptance未完了のためMVP完成とは扱わない。

既にmerge済みのPR #11/#12 branchを新しい実装の土台として再利用しない。各作業は最新`main`から新branchを切る。

## mainで確立済みの主要境界

### Pairing / identity / discovery

- Windowsは120秒single-use QR challengeを発行し、比較番号をPC側で承認。
- Android client identityはKeystore、Windows server identityはcurrent-user non-exportable CNG key。
- 登録後APIはmTLS。Windowsはpaired-device revocationをrequestごとに再確認。
- mDNS/NSDはrouting metadataのみで、endpoint変更はSPKI pin/client identity/Device ID検証後に保存。
- Pixel 8a実機で、Android Keystore EC鍵に`DIGEST_SHA256`と`DIGEST_NONE`を許可した状態のTLS 1.3 mTLS接続とpaired-PC保存を確認済み。`DIGEST_SHA256`のみではpairing proofは成功してもConscryptの`NONEwithECDSA`署名ができず、最初のAPI handshakeが失敗していた。

### Windows handle-safe filesystem

- 受信rootはローカルNTFS/ReFSのみ。
- traversalはretained directory handle基準。
- junction/symlink/reparse point/hardlink/replacement race/unsafe mount transitionはfail closed。
- private staging + same-volume no-overwrite rename。
- physical root pathをwireへ出さない。

### Windows durable upload authority

- server upload progressのauthorityはWindows SQLite journal。
- `write → FlushToDisk → journal commit → response`。
- ambiguous DB commitではfileを勝手にrollbackせずstartup reconciliationへ委ねる。
- renameがfile commit point。
- rename後journal gapはdestination identity/volume/size/SHA-256を証明できる場合だけCompletedへ収束。
- cancel terminal commitがcleanupより先、device revokeがtransfer cleanupより先。
- shareはpersisted opaque generationで識別し、old rootをpath文字列から再探索しない。

詳細: [Windows durable recovery](architecture/durable-transfer-recovery.md)

### Android durable upload recovery

- local journalはserver progress authorityではなくrecovery capability/intent。
- stable operation/idempotency keyとsource identityをserver create前にdurable化。
- server transfer ID/offsetはserver observation後にmonotonic checkpoint。
- actual `persistedUriPermissions`をSAF capability authorityとして確認。
- resume前にsaved PC identityをpinned TLS/client identity + `/api/v1/info`で再検証。
- more bytes前にsource full hashを再検証。
- server aheadは採用可能、server behindはfail closed。
- cancel intentはremote side effectより先にdurable化し、Completedとの競合ではCompleted優先。
- corrupt/unknown/oversized/multiple journalは新規transferをblock。
- interrupted downloadは同じgeneric SAF destinationへprocess-kill resumeしない。

詳細: [Android durable recovery](architecture/android-durable-transfer-recovery.md)

## PR #14 — Android → PC text / URL

既存Protocol v1の拡張性を維持し、実装方向だけAndroid → PCに限定する。

実装境界:

- `POST /api/v1/text`を既存mTLS listenerへ追加。
- `TextSend` permissionをWindowsで強制。
- `plainText` / `url` kindを維持。
- content上限65,536、JSON body上限128KiB。
- URL kindはabsolute http/httpsのみ。embedded credentialsを拒否。
- AndroidはSPKI pin + Keystore client identityでPOST。
- raw transport failure時のみ同じidempotency keyで1回retry。
- Windowsはactive runtime内でbounded per-device idempotency windowを持ち、同一key/payloadのretryではUI通知を重複させない。key再利用でpayloadが変われば`IDEMPOTENCY_CONFLICT`。
- Windows UIは最新受信内容を表示しcopy可能。
- URLを受信しただけではブラウザーを開かない。PCユーザーが明示ボタンを押した場合のみopen。
- tray通知には本文を出さず、汎用的な「テキストまたはURLが届いた」通知だけ表示。
- file transfer pending/recovery stateはtext deliveryのjournalではないため、Androidからのtext送信自体はfile transferと独立して許可。

今回やらないもの:

- PC → Android delivery;
- Android listener / long-poll;
- user-visible persistent text history;
- `GET /api/v1/text/history` product implementation;
- `ACTION_SEND` / `ACTION_SEND_MULTIPLE` integration。

OpenAPI/generated DTOは既に`SendText`/`TextEntry`/history拡張を表現しているため、PR #14では変更しない。

## PR #14 自動テスト

追加:

- Android `TextMessageRulesTest`
  - plain text bound;
  - URL http/https validation;
  - credentials/relative/non-http scheme拒否;
  - unknown kind拒否。
- Windows `TextMessageApiIntegrationTests`
  - real Kestrel + mTLSでPOST;
  -同一idempotency key retryが同一`TextEntry`へ収束しpresentationが1回だけ;
  - same key/different payloadが409;
  - invalid URLが400;
  - `TextSend` permission無しで拒否。

既存CI:

```sh
dotnet format PhoneTransfer.slnx --verify-no-changes
dotnet build PhoneTransfer.slnx --configuration Release
dotnet test tests/windows/PhoneTransfer.Tests --configuration Release
python scripts/validate_protocol.py
python scripts/generate_protocol.py --check
git diff --check
gradle -p apps/android spotlessCheck :protocol:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
```

CI greenとphysical acceptanceを混同しない。

## 次にやること — 最優先

### 1. PR #14を閉じる

- latest HEADのprotocol / Windows / Android CIを全greenにする。
- mainとの差分を再監査し、text endpoint以外の認証/transfer authorityを崩していないことを確認。
- URL auto-openが存在しないこと、tray本文露出がないこと、OpenAPI/generated driftがないことを確認。
- Ready化までは可。mergeはユーザーの明示指示後のみ。

### 2. Physical-device acceptance checklist

少なくとも前提端末、手順、期待結果、実測結果、証跡を記録できる形にする。

### 3. Android ↔ Windows実機acceptance

QR pairing / comparison code / TLS 1.3 mTLS / paired-PC保存はPixel 8aで確認済み。残りを継続する。

ファイル系:

- QR/comparison-code pairing（Pixel 8aで確認済み）;
- real Wi-Fi mDNS;
- mTLS（Pixel 8a ↔ WindowsのTLS 1.3で確認済み）;
- basic upload/download;
- seekable / nonseekable-reopenable SAF provider;
- persistable grant accept/reject;
- hashing/create/chunk/completed境界のprocess kill;
- Android process/device reboot;
- Windows process/PC reboot;
- Wi-Fi loss/reconnect;
- screen-off / FGS timeout;
- multi-GB / disk-full / sleep-resume / NTFS-ReFS / mounted-volume rejection。

text/URL系:

- Android plain text send;
- http/https URL send;
- invalid URLが送信UI/serverで拒否される;
- Windows window表示中の受信;
- tray-hidden時の汎用通知;
- copy;
- URLは受信だけではopenしない;
- 明示的な「URLを開く」で既定browserを起動;
- 一時的なnetwork failure後のretryで同一runtime内duplicate presentationが発生しない。

## その次の実装

- Windows transfer-journal retention / maintenance;
- DHCP/Wi-Fi adapter変更時のauto-rebind;
- entry-list pagination;
- Android `ACTION_SEND` / `ACTION_SEND_MULTIPLE`;
- 必要ならtext history / PC→Android delivery;
- 必要ならbounded recovery scheduler;
- branch protection / required CI / state-store ACL統一 / optional Actions SHA pinning。

## 運用上の安全側制限

- Windows transfer journalは4,096 records上限。容量回避のためactive DBを削除しない。
- cleanupを証明できないtransfer record/stagingは安全側に保持することがある。
- Android recoveryはuser-driven。unrestricted background resurrectionは実装しない。
- interrupted download continuationは未実装。
- text/URLのuser-visible historyは未実装。現在のdedupe windowはprocess-local/boundedで、durable message queueではない。

## 正本

- [Implementation status](implementation-status.md)
- [Windows durable recovery](architecture/durable-transfer-recovery.md)
- [Android durable recovery](architecture/android-durable-transfer-recovery.md)
- [Protocol v1](protocol/v1.md)
- [Threat model](security/threat-model.md)
- [ADR](decisions/)
