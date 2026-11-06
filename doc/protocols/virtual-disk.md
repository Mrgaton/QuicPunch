# Disco virtual

QuicNet exposes a folder or volume through a dedicated authenticated peer session (protocol GUID `00000000-0000-0000-0000-000000000009`). The desktop and Android applications register the handler through their shared `WebUiServer`. The feature is available at `/virtual-disk.html`.

## Authority and consent

- The owner selects a trusted recipient, a folder or volume, read-only or read/write access, optional deletion, and an expiration. Defaults: read-only, no deletion, 24 hours.
- Only the local owner or a trusted contact whose locally saved profile is **Administrator** can explore the host filesystem. Assigning that profile authorizes exploration of all locations accessible to the QuicNet process, and sharing them without another prompt. It does not elevate the process above OS filesystem permissions.
- An administrator creating a share remotely can only grant it to its own authenticated certificate. A request cannot impersonate another recipient or declare itself an administrator.
- Friends cannot enumerate drives, inspect arbitrary absolute paths or grant shares. Session acceptance and the `virtual-disk` automatic session permission only permit connecting; they never grant exploration or access to an unshared folder.
- Every listing, read block, write block and commit checks host-side trust and the current share. Administrator-created grants also require the administrator role to remain active. Downgrading the role stops further use of those grants. Locally created explicit grants remain independent of that role.
- Revoking a share closes its open transfers. Expired grants and lost trust reject subsequent operations. Permission changes cannot recall bytes already delivered or remove copies cached by a client application.

ACLs live in the node's `virtual-disks.json`, addressed by authenticated certificate hashes. There is no separate WebDAV permission database. The local activity view retains the most recent 500 relevant operations in memory; it is not a durable forensic log.

## Filesystem and concurrency

Share paths are relative to a checked root. Traversal, alternate data streams, UNC/device paths, trailing-dot/space aliases, and reparse points (including junctions and symbolic links) are rejected. The service cannot create links or hard links. Internal upload filenames are hidden and inaccessible through share operations.

Multiple readers hold independent file handles. A reader retains the previous file when QuicNet atomically replaces it, so it does not combine bytes from different versions. On Windows, the read sharing mode prevents ordinary in-place writers from modifying a file while these reads are open.

Uploads go to unique sibling temporary files. Sequential blocks, declared length and a SHA-256 comparison between received bytes and staged bytes are checked before publication. Replacing a file requires its expected content hash; creating a file fails if the name has appeared in the meantime. Two competing writers cannot silently overwrite each other through QuicNet. Publication uses `File.Replace` for existing files and non-overwriting `File.Move` for new files. Filesystems that do not support the replacement fail the operation.

Exclusive file leases last 120 seconds and can be refreshed. They are enforced by canonical host paths across overlapping shares, the web UI and WebDAV. They are tied to the authenticated actor, share and a random token; knowing a filename cannot release another actor's lease. Writes after a lease expires must obtain a new lease or restart with a current version. File leases do not prevent independent local programs from renaming files, or make arbitrary OS programs participate in a distributed transaction.

Transfers use 128 KiB blocks, with at most 4 open transfers per identity and 32 per host. An upload is limited to 10 GiB; unfinished uploads reserve at most 20 GiB in total. Idle transfers expire after two minutes. At most 16 file leases per identity and 128 per host are allowed. Directory pages contain up to 200 entries.

`virtual-disk-uploads.json` journals only exact generated temporary filenames. Restart cleanup deletes abandoned uploads in validated non-link parents. Disconnect/failure recovery never publishes an incomplete upload; interrupted uploads currently restart instead of resuming. No recycle bin or historical file-version retention is implemented. Deletion is explicit, permanent, and directories must be empty.

The filesystem boundary assumes that local processes with access to the host's files and configuration are trusted. Path checks are not an OS sandbox against a malicious local process racing directory replacement. Content checks and leases coordinate QuicNet operations; they cannot provide a universal compare-and-swap transaction against unrelated local applications.

## Native mounting

The local UI creates an unpredictable 256-bit capability for one peer/share. The WebDAV adapter listens through the existing **127.0.0.1-only** UI listener and forwards all operations through the same host authority. Its private URL is a bearer capability: keep it on the receiving device. Cross-origin browser requests are rejected, and file bodies are always attachments, never active HTML in the UI origin. The network hop between peers remains inside the authenticated encrypted QuicPunch session.

On Windows, **Montar unidad** runs `net use * <local UNC> /persistent:no` without elevation or firewall/registry changes. The WebClient service must already be available. A failed automatic mount returns the private address for a local WebDAV client. Unmounting invalidates the capability and removes a mapping created by QuicNet. Stopping QuicNet invalidates all its capabilities. Android uses the built-in explorer; mounting into Android's system filesystem is not implemented.

Supported methods: OPTIONS, PROPFIND (finite depth), GET, HEAD, PUT, MKCOL, DELETE, file LOCK and UNLOCK. PUT to an existing file requires an expected ETag or a valid host-side lease, whose content version supplies that precondition. Permission revocation applies to mounted access too. The adapter does not advertise full WebDAV class 2 compliance: directory locks, MOVE/COPY and arbitrary PROPPATCH properties are not supported. Some editors using save-by-rename need these operations; use the built-in upload workflow in that case. Native metadata caching and filesystem I/O buffering remain controlled by the OS client.

## Validation

```powershell
./scripts/test.ps1 -Tests virtual-disk,webui-security

# Also exercise a real temporary Windows drive mapping, read, create and replace.
$env:QUICNET_TEST_NATIVE_DRIVE = '1'
./scripts/test.ps1 -Tests virtual-disk

# UI fixture tests; set the existing project browser/runtime overrides if needed.
node QuicPunchTests/Tests/VirtualDiskBrowserTests.mjs
```

The host tests cover ACL isolation, role downgrade, expiration, revocation of open transfers, traversal/junction rejection, read-only and delete permissions, simultaneous snapshots, stale writers, shared path leases, incomplete/corrupt uploads, crash cleanup, generated JSON framing, duplex requests, CSRF/origin checks and case-preserving WebDAV paths. Browser fixtures cover navigation, administrator selection, grants, uploads, escaping untrusted names and desktop/mobile layout. These tests do not replace an interoperability test between physical Android and desktop devices over WAN/Tor.
