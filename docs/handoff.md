# 開発引き継ぎ — Windows durable transfer recovery / PR #11

更新日: 2026-09-08

## 作業対象と禁止事項

- リポジトリ: `shinp-dev/phone-transfer`
- main: PR #10までマージ済み (`c7db4bb`)
- 継続ブランチ: `feature/durable-transfer-recovery`
- 既存PR: #11「Durable transfer recoveryの状態機械を実装」
- 新しいブランチ・PRを作らない。mainへマージしない。force pushしない。
- 全CIと自己監査を終えるまではDraft。実機受入はCIとは別に報告する。

## 実装した境界

`WindowsServerRuntime`は`DurableFileTransferService`と独立した`transfers.db`を所有し、起動復旧が完了してからlistenerを開始する。認証は既存の`devices.db`で継続する。

- `DurableTransferMachine`: 状態・offsetの不変条件と遷移を集約。
- `SqliteTransferJournal`: schema v1、WAL/FULL、5秒busy timeout、短い明示transaction、revision CAS、永続idempotency、exclusive process lease。
- `WindowsDurableStaging`: random capability、root/directory/file/destination-parentのvolume/file ID、既存ACL検査、handle-relative reopen/truncate/no-replace rename。
- `DurableTransferRecovery`: DB offsetへのtail正規化、missing/short/identity mismatchのfail closed、rename済みdestinationのidentity/size/hash証明、terminal cleanup、bounded orphan処理。
- `WindowsShareConfigurationStore`: v2でopaque generationを永続化。v1はローカルread時にatomic upgradeする。旧generation/旧rootへのresumeやpath再探索はしない。

基本順序はwrite → FlushToDisk → SQLite commit → response。write/flush失敗はtruncate + flush成功時だけretryable。DB commitの成否が不明ならfileをrollbackせず、serviceを停止して次回起動のDB authorityへ委ねる。

renameがfile commit point。rename後にDB更新が失敗してもdestinationを削除しない。再起動ではfile ID・volume・size・SHA-256が一致する場合だけCompletedへ収束する。

cancelはDB terminal化が先。revokeはdevices.dbの失効が先で、hash/cleanupを待たず以降のHTTP認可を拒否する。転送単位のgateと短いdevice commit fenceを使い、global lockで全体hashを囲まない。登録時刻も束縛して、同じdevice/certificateを再登録しても旧transferを復活させない。

shutdownは新規mutationを停止してin-flightを収束させる。durable stagingはDisposeで削除しない。強制終了時の正しさはstartup reconciliationが担う。

## テストと監査

`DurableTransferRecoveryTests`は実SQLiteを新規instanceで開き直し、create/chunk/flush/offset/verify/hash/rename/completed/cancel/revoke/reconciliationの各境界、disk-full相当のpartial write、flush/truncate/cleanup失敗、DB commit前後の例外を検証する。二回目の再起動、idempotency競合、destination impostor、share変更、同時PATCH、hash中のrevokeと別transfer進行も対象。

`WindowsDurableStagingTests`は実Windows handleとSQLiteでreopen/truncate、stale capability、file ID差し替え、hardlink/symlink/junction、ACL変更、root変更、orphan、no-overwrite、rename→DB gapを検証する。PR #7の既存adversarial testsは維持する。

自己監査・CIの最終結果はPR本文を確認する。ローカル作業環境には.NET SDKがないため、Windows上のformat/build/testは既存GitHub Actionsを使用する。成功していないgateを成功扱いしない。

## 運用上の安全側制限

- 4,096 journal records。自動evictionはせず、idempotency mappingを保持する。
- 1 deviceあたりactive 4件、staging予約256 GiB。Failed/Cancelledの予約はcleanup失敗によるquota回避を防ぐため保守的に保持する。自動retention/maintenanceは未実装。
- orphan処理はcurrent rootのdirect entriesを最大4,097件確認し、4,096件を超えたら起動を拒否。全filesystem scanをしない。
- 不明なprivate layout/ACL/link、参照済みidentity mismatch、旧shareのstagingはquarantine相当として隠したまま残す。旧rootを文字列で再openしない。
- Completedの現share destinationが消失・改変され、証明できなければDB terminal stateを維持したままAPI readinessを拒否する。通常ファイルを再作成・上書きしない。
- 上限を避けるためにactiveなtransfers.dbを削除しない。容量回収の運用機能は後続の明示的な設計対象。

## 今回変更していないもの

OpenAPI/generated DTOとAndroid sourceは変更しない。Androidの既存SAF/Foreground Service basic uploadと曖昧response後のGET照合は同じwire契約で動く。

Android process-kill resume、transfer DB、WorkManager、ACTION_SEND/MULTIPLE、queue UX、text/URL/history、Windows UI全面変更は未実装・今回の対象外。

## 検証コマンド

```sh
dotnet format PhoneTransfer.slnx --verify-no-changes
dotnet build PhoneTransfer.slnx --configuration Release
dotnet test tests/windows/PhoneTransfer.Tests --configuration Release
python scripts/validate_protocol.py
python scripts/generate_protocol.py --check
git diff --check
```

既存CIのAndroid jobもgreenを確認する。

## 残る実機受入

Windows 11 + Androidの実機でbasic upload/download、multi-GB、Wi-Fi断、PC sleep/reboot、実process kill、実disk-full、flushを正しく実装する実ストレージでの電源断を確認する。NTFS以外にReFS、実mounted-volume拒否、private ACL、外部セキュリティソフト干渉も確認する。

Android SAF providerの再open・grant、screen-off、通知許可、FGS timeout/process killは別途確認する。Android自身のprocess-kill後の再開機能を実装済みと説明しない。

詳細正本: [durable recovery](architecture/durable-transfer-recovery.md)、[ADR 008](decisions/008-filesystem.md)、[threat model](security/threat-model.md)、[protocol](protocol/v1.md)。
