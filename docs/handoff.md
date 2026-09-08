# 開発引き継ぎ — durable recovery merge後

更新日: 2026-09-09

## 現在地

- リポジトリ: `shinp-dev/phone-transfer`
- current main: `0591396f40097d834ba583a10f2dd4e5953b6a3e`
- PR #11: Windows durable transfer recovery — **merged**
- PR #12: Android process-kill durable upload recovery — **merged**
- 現在、durable upload recoveryの追加実装を継続する既存feature branchはありません。
- コアのfile-transfer実装は大きく揃っていますが、実Windows/Android端末でのphysical acceptanceが未完了のためMVP完成とは扱いません。

古い「PR #11を継続」「Android process-kill recovery未実装」という前提は無効です。

## mainに入っている主要境界

### Pairing / identity

- Windowsは120秒のsingle-use QR challengeを発行。
- AndroidはECDSA proofを使ってpairingし、比較番号をPC側で確認して承認。
- Windows server identityはcurrent-user non-exportable CNG key。
- Android client identityはKeystore。
- 登録後のAPIはmTLS。
- Androidは保存済みSPKI pin、client identity、`/api/v1/info`のDevice IDを検証してからendpoint変更を保存。
- Windowsはpaired-device revocationをrequestごとに再確認。

### Discovery

- Windowsは選択したprivate IPv4 interfaceで `_phone-transfer._tcp` をmDNS広告。
- Androidは`NsdManager`で発見。
- mDNSはrouting metadataとしてのみ扱い、発見値そのものをidentityとして信用しない。

### Windows handle-safe filesystem

- 受信rootはローカルNTFS/ReFSのみ。
- UNC、volume root、application-data overlap、設定時のreparse ancestryを拒否。
- traversalはretained directory handle基準。
- junction / symlink / reparse point / hardlink / replacement race / unsafe mount transitionをfail closed。
- stagingはprivate ACL領域。
- completionはsame-volume no-overwrite rename。
- physical root pathはwireへ出さない。

### Windows durable upload authority

`WindowsServerRuntime`は`DurableFileTransferService`と独立したSQLite transfer journalを所有し、startup reconciliation完了後にlistenerを開始する。

主要不変条件:

- server upload stateのauthorityはWindows durable journal。
- `write → FlushToDisk → journal commit → response`。
- DB commitの成否が曖昧ならfileを勝手にrollbackせず、serviceを止めて次回startup reconciliationへ委ねる。
- renameがfile commit point。
- rename後のjournal更新に失敗してもdestinationを削除しない。
- restart時はfile ID / volume / size / SHA-256が一致した場合だけCompletedへ収束。
- cancelはserver terminal state commitがcleanupより先。
- revokeはdevice authorization失効がtransfer cleanupより先。
- upload ownershipはdevice IDだけでなくcertificateとregistered-at identityにも束縛。
- shareはpersisted opaque generationで識別し、古いrootをpath文字列から再探索しない。

詳細: [Windows durable recovery](architecture/durable-transfer-recovery.md)

### Android SAF / Foreground Service

- upload sourceは`ACTION_OPEN_DOCUMENT`。
- download destinationは`CREATE_DOCUMENT`。
- content URIをfilesystem pathへ変換しない。
- long-running I/Oはnon-exported `dataSync` Foreground Serviceが所有。
- uploadはsourceをhash/countしてからcreateし、再openしてbounded chunksを送る。
- downloadはstream中にlengthとSHA-256-derived ETagを検証し、成功時のみCompletedを表示。

### Android durable upload recovery

Android側journalはserver progress authorityではなく、process-kill後に安全にWindows authorityへ再接続するためのlocal capability/intent記録。

主要不変条件:

- local operation IDとstable idempotency keyをserver createより前にdurable化。
- source name / size / SHA-256をcreateより前にdurable化。
- server transfer IDとobserved committed offsetはserver response確認後にcheckpoint。
- actual `persistedUriPermissions`をSAF resume可否のauthorityとして確認。
- restart/resume前にsaved PC identityをpinned TLS/client identity + `/api/v1/info`で再検証。
- more bytes送信前にsource full hashを再検証。
- server offset > local offsetはcrash gapとして採用可能。
- server offset < local offsetは不整合としてfail closed。
- server Completed済みならAndroid sourceを再openせずcompletion receiptへ収束可能。
- cancel intentはremote DELETE/create/status side effectより先にdurable化。
- stale writerはcancel intentやcommitted offset/server identity/source identityを巻き戻せない。
- cancelとCompletedが競合した場合はserver Completedを優先。
- corrupt/unknown/oversized/multiple journalは空扱いせず新規transferをblock。
- completion receiptはprocess recreation後も再表示でき、次の明示的transfer開始時にだけ退役。
- interrupted downloadは同じgeneric SAF destinationへprocess-kill resumeしない。service/state/UIすべてで非resumable。

詳細: [Android durable recovery](architecture/android-durable-transfer-recovery.md)

## 自動テスト / CIの到達点

PR #12 final HEAD `4c65f0369c96a01f0c1e9cc73a0b3ba2b7e40f5e` はGitHub Actions CI #231で全job成功後にmergeされた。

最終CIでは以下を確認済み:

```sh
dotnet format PhoneTransfer.slnx --verify-no-changes
dotnet build PhoneTransfer.slnx --configuration Release
dotnet test tests/windows/PhoneTransfer.Tests --configuration Release
python scripts/validate_protocol.py
python scripts/generate_protocol.py --check
git diff --check
gradle -p apps/android spotlessCheck :protocol:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
```

Windows recoveryではSQLite reopen、write/flush/offset/verify/hash/rename/completed/cancel/revoke、destination impostor、share change、concurrent mutation、orphan/ACL/link等を自動検証している。

Android recoveryではstable idempotency、lost create response、server ahead/behind、source identity、SAF recovery state、cancel durability、cancel/completion race、completion receipt、corrupt/oversized journal、stale state callback、service-level download resume拒否等をJVM testsで検証している。

ただし自動テストはphysical acceptanceの代替ではない。

## 次にやること — 最優先

### 1. Physical-device acceptance checklistを作る

テスト項目・前提端末・手順・期待結果・実測結果・証跡を記録できる形にする。単なる「実機で動いた」ではなく、failure point別に残す。

### 2. Android ↔ Windows 実機acceptance

最低限:

- QR pairing / comparison code;
- real Wi-Fi mDNS discovery;
- mTLS request;
- basic upload/download;
- seekable / nonseekable but reopenable SAF source;
- persistable grantを許可するprovider / 拒否するprovider;
- upload hashing中process kill;
- create直後process kill;
- chunk途中process kill;
- server Completed後Android notification前kill;
- Android app/process restart;
- Android device reboot;
- Wi-Fi interruption / reconnect;
- screen-off;
- dataSync FGS timeout;
- Windows process restart / PC reboot;
- PC sleep/resume;
- multi-GB transfer、可能なら4 GiB+;
- disk-full / staging failure;
- NTFS / ReFS;
- real mounted-volume拒否;
- private ACL / security software干渉。

実機で見つかった問題は、再現手順と期待契約を固定してから別branch/PRで修正する。

## その次に残る実装

### Windows journal retention / maintenance

現在のjournalは4,096 records上限。自動evictionはしない。idempotency mappingとterminal/recovery proofを壊す雑な削除は禁止。

retentionを実装する場合は少なくとも以下を設計してから着手する:

- どのterminal recordをいつ消してよいか;
- idempotency replay windowをどう保証するか;
- staging cleanupが証明できないrecordの扱い;
- quota reservationとの関係;
- maintenance中crash時の再起動契約;
- active/recoverable recordを絶対に消さない境界。

### その他

- DHCP/Wi-Fi adapter変更時のWindows自動rebind;
- entry-list pagination;
- ACTION_SEND / ACTION_SEND_MULTIPLE;
- text / URL transfer;
- transfer/history UI;
- 必要ならbounded recovery scheduler;
- main branch protection / required CI checks;
- state store ACLの明示統一;
- optional third-party Actions SHA pinning。

これらはdurable upload correctnessと分離して進める。

## 運用上の安全側制限

- Windows durable journalは4,096 recordsまで。active DBを容量回避目的で削除しない。
- 1 deviceあたりactive transfer数とstaging reservationには上限がある。
- cleanupを証明できないFailed/Cancelled recordは保守的にquotaを保持する場合がある。
- orphan inspectionはbounded。全filesystem scanはしない。
- identity/ACL/layoutが不明なprivate stagingは安全側に残す。
- Completed destinationを再証明できない場合、通常ファイルを再生成・上書きしない。
- Android recoveryはユーザーがappを再度開いたときに行う。unrestricted background resurrectionは実装していない。
- interrupted download continuationは未実装であり、durable resumeを主張しない。

## 開発時の注意

- 作業開始時は必ず最新`main`を取得して新branchを切る。
- 既にmerge済みのPR #11/#12 feature branchを新規作業の土台として再利用しない。
- durable recoveryのauthority/orderを崩す変更は、見た目の単純化を理由に行わない。
- OpenAPI変更が不要な作業ではgenerated protocolへ差分を出さない。
- CI greenとphysical acceptanceを混同しない。
- 実機未確認項目を「確認済み」と記録しない。

## 正本

- [Implementation status](implementation-status.md)
- [Windows durable recovery](architecture/durable-transfer-recovery.md)
- [Android durable recovery](architecture/android-durable-transfer-recovery.md)
- [ADR 008 filesystem](decisions/008-filesystem.md)
- [Threat model](security/threat-model.md)
- [Protocol v1](protocol/v1.md)
