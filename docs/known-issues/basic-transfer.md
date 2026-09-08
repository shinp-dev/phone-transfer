# Current transfer follow-ups

Updated: 2026-09-09 JST

This file replaces the old “basic transfer” follow-up list. Windows durable recovery, Android process-kill upload recovery and Android -> PC text/URL are now implemented on `main`; they are no longer deferred items.

For the audited current boundary see [final audit](../final-audit.md). For the remaining MVP gate see [physical-device acceptance](../physical-device-acceptance.md).

## Physical acceptance still required

- real Windows 11 + Android QR/comparison-code pairing and mDNS rediscovery;
- real SAF upload/download providers;
- Android process-kill upload resume;
- Windows process restart / PC reboot recovery;
- interrupted download remains non-resumable to the same destination;
- Wi-Fi interruption and reconnect;
- screen-off / notification / dataSync timeout behavior;
- multi-GB and disk-full behavior;
- Windows sleep/resume;
- NTFS/ReFS and real mounted-volume/reparse rejection;
- Android -> PC text/URL, tray notification, copy and explicit-only URL open.

## Deliberately deferred implementation

- Windows transfer-journal retention / maintenance. Current bounded journal records must not be deleted casually because terminal proof, idempotency replay and staging ownership depend on them.
- DHCP/Wi-Fi adapter-change automatic rebind; current UI supports manual reconnect/restart on the selected adapter.
- entry-list pagination beyond the current bounded first page.
- Android `ACTION_SEND` / `ACTION_SEND_MULTIPLE` and multi-file queue UX.
- user-visible persistent text history and PC -> Android text delivery.
- optional unattended/bounded recovery scheduler.
- public release packaging/signing/installer polish.
- repository hardening such as protected `main`, required CI checks and optional immutable SHA pinning for third-party Actions.

## Low maintenance debt

- Legacy process-local upload helpers still exist alongside the durable production paths. They are not currently wired as production upload authority, but later removal/isolation would reduce accidental-rewire risk.
- Some Android APIs/dependencies produce non-blocking deprecation/version hints in CI; current lint has no errors or warnings.
- Pairing bootstrap rate limiting is intentionally small/global and can sacrifice availability under same-LAN abuse without weakening proof/approval authentication.
- Text duplicate suppression is bounded/process-local and is not a durable queue/history feature.

## Protocol documentation note

Runtime endpoint authority is the QR/mDNS API endpoint on port `58443`. If an OpenAPI `servers` example differs, it is documentation metadata only and must never be used by clients as runtime identity/routing authority. The Android product uses the QR/saved/discovered endpoint after pin and Device-ID verification.
