# ADR 008-filesystem: Windows handle containment

Date: 2026-09-07
Status: Accepted design; implementation tracked separately.

## Decision

Use NTFS/ReFS local shares only, reject reparse points, retain ancestor handles and validate final opened handle path.

Share configuration may be implemented before network file APIs, but selecting a folder does not make it remotely accessible. Configuration-time checks reject UNC paths, volume roots, non-NTFS/ReFS filesystems, reparse points in the selected root ancestry and overlap with the application's own data directory. These checks are defense in depth only and never replace handle-based containment during file operations.

## Alternatives and rationale

Path.GetFullPath containment and FileAttributes checks alone have a check/use race. ResolveLinkTarget alone does not prevent a subsequent junction replacement.

## Consequences

Do not expose file APIs until Windows-specific handle-safe implementation and adversarial integration tests pass. Deny unsupported filesystem roots, UNC paths and unsafe share setups. No overwrite and same-volume staging are mandatory.

## Handle adapter (2026-09-08)

The internal `IShareFileSystem` contract lives in Application; the implementation and all native calls live in Infrastructure/Storage. No host, UI, Android, HTTP route or wire model is changed. `RelativeSharePath` and the configuration-time reparse checks remain independent defenses.

### Containment invariants

1. A session strictly parses the locally configured drive path, opens the drive root with `CreateFileW`, requires a local NTFS/ReFS volume-root handle (normalized volume-GUID path; no SUBST subdirectory), then opens every root ancestor as a single component with `NtCreateFile(OBJECT_ATTRIBUTES.RootDirectory = parent)`.
2. Every subsequent traversal uses the same single-component relative open, `FILE_OPEN_REPARSE_POINT`, and inspection of the returned handle. `FileAttributeTagInfo` rejects every reparse tag, including junctions, volume mount points and file/directory symlinks. No check-then-open by absolute pathname exists.
3. The volume root, configured root ancestry, and operation ancestry remain open without write/delete sharing. Existing conflicting handles cause failure. Once an open succeeds, ordinary rename, deletion, replacement and reparse mutation cannot acquire the necessary incompatible access. No `FILE_SHARE_DELETE` or POSIX rename semantics are enabled. Local administrator/kernel compromise and same-user malware remain outside the threat boundary.
4. `GetFinalPathNameByHandleW(FILE_NAME_NORMALIZED | VOLUME_NAME_GUID)` additionally checks that the child is immediately below its retained parent's actual path, and `FileIdInfo` checks the volume serial identity. This is a secondary assertion of the handle walk, not string-only containment. Unsupported identity/path queries fail closed. FileStandardInfo rejects pending deletion and files with multiple hard links. Read handles do not share write/delete, so reads use a stable file object.
5. Completion calls `NtSetInformationFile(FileRenameInformation)` on the still-open staging file with the retained destination parent handle, one validated filename and `ReplaceIfExists = FALSE`. The kernel enforces same-volume/no-overwrite atomically. `DestinationExists` is advisory and does not authorize completion; a destination created after the check still wins. A conflict preserves staging for retry. No file bytes are written through a destination path.

### Ownership and concurrency

A session owns its root-chain SafeFileHandles, its optional private directory handle and all live child objects (maximum 64). Each child solely owns its file SafeFileHandle and its traversal chain. All I/O, native handle embedding, enumeration and disposal use one session gate; no raw handles, physical paths or FileStreams escape into Application. Embedded parent handles additionally use DangerousAddRef/Release for the native call. Child disposal closes leaf before ancestors; session disposal invalidates/closes all children before root ancestors. Disposal is idempotent. Temporary native buffers and security descriptors use explicit finally cleanup. No asynchronous callbacks retain native memory.

### Private staging

Each writing session creates a fresh `.phone-transfer-staging-<random UUID>` directory directly below its pinned root using atomic `FILE_CREATE`; an existing name is never adopted. The directory and new `.part` files receive a protected, explicit current-user-only full-control DACL at creation (`ConvertStringSecurityDescriptorToSecurityDescriptorW`, supplied to NtCreateFile). Staging is necessarily on the destination volume. This is share-private staging, not the per-user application-state directory.

The reserved prefix is rejected in all client path components. Enumeration opens and validates candidates and omits unsafe/private entries. The normalized actual child name is checked too, preventing 8.3 aliases from bypassing private-name exclusion. Listing is capped at 200 results and 4096 inspected directory records; it is advisory, not a snapshot, and pagination is intentionally deferred. Completed files retain the restrictive current-user ACL; broadening destination ACLs is not implicit.

Abandonment marks the owned staging file for deletion by handle. Session disposal removes the now-empty private directory by handle; cleanup failures close all resources and report an error. A process crash may leave a private, API-inaccessible directory. This increment does not reopen old staging, record durable offsets or implement crash cleanup/recovery. Future resume must introduce an ownership-verified private-staging capability, never arbitrary OS paths or an unvalidated reopen of a predictable staging name. Flush/rename primitives alone do not claim crash durability for a transfer transaction.

### Evidence and limits

Windows integration tests cover deep Unicode/NFC paths, invalid syntax, external junctions at root and intermediate traversal, file/directory symlinks, hard links, root replacement after configuration, retained root/ancestor/file locks, preexisting writers, no-overwrite after deterministic check/use interleaving, private ACL/name/alias exclusion and handle cleanup after success/failure/disposal. Symlink/junction creation is mandatory on Windows CI: unavailable privileges are visible failures, not skipped tests. 8.3 checks log when the filesystem has no alias. Actual mounted-volume provisioning and ReFS require a separately provisioned Windows volume; CI uses NTFS and junctions (the same IO_REPARSE_TAG_MOUNT_POINT), while runtime rejects every reparse tag and nonlocal/unsupported volume. These limitations do not enable a fallback path.

References: [NtCreateFile](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntcreatefile), [FileRenameInformation](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information), [GetFileInformationByHandleEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getfileinformationbyhandleex).
