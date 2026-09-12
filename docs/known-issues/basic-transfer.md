# Basic transfer follow-ups

These items are intentionally outside the first Windows basic file-transfer and Android SAF/Foreground Service increments and are not release-complete claims:

- Durable transfer DB, restart resume, committed-offset truncate/reconciliation and hard-crash staging cleanup.
- Android process-kill recovery; the current Foreground Service and transfer-status bus are process-local.
- The v1 upload contract requires total size and SHA-256 before create. Android therefore reads a SAF source once to hash/count it and reopens the same URI to upload. Nonseekable streams are supported, but providers that cannot reopen the selected document are not.
- SAF download completion is not atomically renameable across arbitrary providers. A failed output is best-effort truncated; provider refusal can leave a partial destination, which is never reported as successful.
- ACTION_SEND/MULTIPLE share-sheet integration is not yet implemented. In-app multi-file selection and durable sequential upload queueing are supported.
- Disk-full and crash-between-rename-and-durable-record recovery.
- Immediate cancellation semantics for a download request that was already streaming when a device is revoked; subsequent requests are denied immediately.
- Moving large-file Windows verification out of the simple process-local transfer lock as part of the durable/background transfer design.
- Physical Windows 11/ReFS/mounted-volume and real Android multi-GB/SAF-provider acceptance.
- The OpenAPI `servers` example still uses the historical documentation port; runtime endpoint authority remains the QR/mDNS API endpoint on 58443. Correct the example in a protocol-only cleanup without changing generated DTOs.
