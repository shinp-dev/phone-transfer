# Development handoff — physical acceptance gate

Updated: 2026-09-09 JST

## Current state

- Repository: `shinp-dev/phone-transfer`
- Audited main: `7c5f85f1c09215d0da24e8d9efe338754a4b17de`
- PR #11: Windows durable transfer recovery — merged
- PR #12: Android process-kill durable upload recovery — merged
- PR #13: documentation/status refresh — merged
- PR #14: Android -> PC text/URL — merged
- Main post-merge CI #242: Protocol / Windows / Android all green
- Final code/feature audit: no Critical, High or merge-blocking Medium in the code-auditable current production paths
- **Next gate: real Windows 11 + Android physical-device acceptance**

Do not reuse old merged feature branches for fixes. Any physical finding should start from the then-current `main` in a new branch/PR.

## What is implemented

### Pairing and trust

- single-use 120-second QR challenge;
- proof-of-possession and explicit PC comparison-code approval;
- Android Keystore client identity;
- Windows non-exportable current-user CNG server key;
- QR-originated SPKI pin;
- authenticated mTLS API;
- request-by-request paired-device revocation checks;
- mDNS/NSD endpoint rediscovery followed by identity re-verification.

### File transfer and containment

- configurable local NTFS/ReFS Windows share;
- retained-handle Windows traversal with reparse/junction/symlink/hardlink/race fail-closed behavior;
- private staging and no-overwrite commit;
- Android SAF upload/download;
- non-exported `dataSync` Foreground Service;
- bounded/authenticated file APIs.

### Durable upload recovery

Windows durable SQLite state is server-side authority. Android durable state is a local operation/capability/reconciliation record, not a competing server-progress authority.

Important invariants already implemented:

- client idempotency/source identity exists durably before create;
- server ACK precedes local observed-offset checkpoint;
- Windows writes/flushes before durable committed offset advances;
- server-ahead/local-behind can converge; server-behind/local-ahead fails closed;
- upload resume requires real SAF grant, PC reauthentication and source full hash;
- Completed can converge without reopening Android source;
- user cancel intent is durable before remote cancel and cannot be cleared by stale writers;
- server Completed wins cancel/completion race;
- corrupt Android recovery data blocks new transfer;
- process-killed download never resumes to the same generic SAF destination.

### Android -> PC text / URL

- `plainText` / `url` through existing mTLS `POST /api/v1/text`;
- `TextSend` permission;
- bounded body/content;
- HTTP/HTTPS-only URL validation and no embedded credentials;
- same-key transport retry and bounded runtime duplicate-presentation suppression;
- Windows latest-message display/copy;
- generic tray notification;
- URL opens only by explicit PC user action.

## Final audit references

- [Final code / feature audit](final-audit.md)
- [Implementation status](implementation-status.md)
- [Physical-device acceptance](physical-device-acceptance.md)
- [Windows durable recovery](architecture/durable-transfer-recovery.md)
- [Android durable recovery](architecture/android-durable-transfer-recovery.md)
- [Threat model](security/threat-model.md)

## Next work order

### 1. Run the minimum physical acceptance flow

Execute M01–M08 in [physical-device acceptance](physical-device-acceptance.md) and record PASS/FAIL evidence.

This is the first priority. Do not add optional product features before learning whether the real Windows/Android path exposes a correctness problem.

### 2. Fix only demonstrated findings

If an item fails:

1. record exact reproduction, OS/device/provider and expected contract;
2. create a new branch from latest main;
3. add the smallest code fix and a regression test when reproducible in automation;
4. run full CI;
5. rerun the failed physical case;
6. update the acceptance table/evidence.

### 3. Run extended physical cases

After M01–M08 pass, prioritize:

- Android device reboot;
- Windows PC reboot and sleep/resume;
- screen-off / notification permission / FGS timeout;
- persistable-grant accept/reject providers;
- nonseekable but reopenable provider;
- multi-GB / 4 GiB+ where feasible;
- disk full;
- NTFS/ReFS and mounted-volume rejection;
- active device revoke;
- DHCP/Wi-Fi adapter change with current manual-reconnect expectation.

### 4. Post-MVP optional increments

- transfer-journal retention/maintenance;
- auto-rebind after adapter/DHCP change;
- entry pagination;
- ACTION_SEND / ACTION_SEND_MULTIPLE;
- optional text history / PC -> Android delivery;
- optional bounded unattended recovery;
- protected main / required checks / immutable Action SHA pinning;
- public release packaging/signing/installer work;
- cleanup/isolation of no-longer-production process-local upload helpers.

## Safety / scope reminders

- CI green is not physical acceptance.
- Never weaken handle-safe containment or durable ordering to make a physical failure disappear.
- Do not delete `transfers.db` merely to recover capacity; retention needs a separate idempotency/recovery-safe design.
- Do not claim interrupted-download continuation; restart with a new destination is intentional.
- Text duplicate suppression is process-local/bounded, not durable history/queue semantics.
- Do not log or publish pairing tokens, certificate material, SAF URIs or sensitive filenames while collecting test evidence.
