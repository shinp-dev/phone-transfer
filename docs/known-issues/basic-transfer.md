# Basic transfer follow-ups

These items are intentionally outside the first Windows basic file-transfer pull request and are not release-complete claims:

- Android SAF selection/export, transfer repository/service ownership and foreground-service lifetime.
- Durable transfer DB, restart resume, committed-offset truncate/reconciliation and hard-crash staging cleanup.
- Disk-full and crash-between-rename-and-durable-record recovery.
- Immediate cancellation semantics for a download request that was already streaming when a device is revoked; subsequent requests are denied immediately.
- Moving large-file verification out of the simple process-local transfer lock as part of the durable/background transfer design.
- Physical Windows 11/ReFS/mounted-volume and real Android multi-GB acceptance.
- The OpenAPI `servers` example still uses the historical documentation port; runtime endpoint authority remains the QR/mDNS API endpoint on 58443. Correct the example in a protocol-only cleanup without changing generated DTOs.
