# User presence

Presence has two independent flags: AFK and acceptance of notifications. The UI
uses green for available, yellow for AFK, red for do not disturb, and a split
yellow/red indicator for AFK with notifications silenced. Labels accompany the
colors. An offline contact has a grey indicator. A connected legacy client whose
status is unknown is labelled “Estado no compartido”.

Click the local user in the sidebar to change do not disturb and the idle
threshold. The same selector is in Settings → Profile, including on mobile.
No separate notification policy is introduced: it uses the existing persisted
`Notifications.DoNotDisturb`, including its notification and ringtone suppression.
Quiet hours and the global notification switch also affect the published
acceptance flag. Per-contact and per-feature notification exceptions stay local.

Windows reads the current session's `GetLastInputInfo`, including activity in
other applications. AFK defaults to five minutes and is configurable from one to
sixty minutes. Activity clears AFK without changing do not disturb. Hosts without
a system idle provider use QuicNet interaction reports, throttled to at most one
per fifteen seconds; the selector explains that fallback. Input contents are not
collected or saved.

`UserPresenceService` samples every three seconds and sends changes over existing
trusted QUIC multiplexers. It refreshes unchanged states every thirty seconds.
Control message type 9 uses the existing 44-byte header: ErrorCode bit 0 means AFK
and bit 1 means notifications silenced. All other fields must be zero. The
receiver checks trust, rejects invalid values and expires the state after ninety
seconds. Closing the transport hides its presence immediately. Older multiplexers
ignore the unknown message type. These frames do not open protocol sessions,
request consent, trigger discovery, or extend application transport idle time.

Presence is excluded from public discovery and private Tor identities, following
the profile's separation between public and Tor identities. Only the two flags
are exchanged; idle duration and notification settings are not sent to peers.

The local API exposes GET/POST `/api/profile/presence` and POST
`/api/profile/activity`; writes use the app's normal CSRF checks. `/api/status`
includes local `presence` and a `peerPresence` map for the UI.

Verification: `--test-notifications` covers AFK boundaries, DND independence,
native idle-provider results, persisted preferences and CSRF. `--test-mux`
exchanges all four combinations over real local QUIC connections, tests trust
and disconnection, and exercises automatic service propagation between two
nodes. `PresenceBrowserTests.mjs` covers desktop/mobile interaction, settings
synchronization, save failure, colors, remote/offline rendering and dark
themes. `VoiceRoomBrowserTests.mjs` checks the existing call room after adding
presence indicators.
