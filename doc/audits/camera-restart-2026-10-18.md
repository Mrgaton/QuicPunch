# Camera selection and restart — 2026-10-18

The reported desktop error was `NotReadableError: Could not start video source`
after stopping and reactivating the camera. This error alone cannot identify a
particular driver failure or another application holding the camera.

## Corrections

- Clicking Camera opens a device selector. Opening or cancelling it does not
  request a video stream. Confirmation acquires the selected device by exact ID;
  the last successful selection is remembered. Browsers can hide device names
  and additional devices until permission has been granted.
- Camera replacement waits for the previous stop operation and frame reader
  cancellation. The encoder, frame pacer, preview and media tracks are released
  before acquiring the replacement source. An old track's `ended` callback cannot
  stop a replacement stream.
- Stop requests carry an optional UUID. The API echoes it in the local
  `screen_share_stopped` event. The initiating UI ignores acknowledgements of
  already applied stops, including duplicates that arrive after a new start.
  Empty stop POST bodies from older clients remain supported.
- Only `NotReadableError` receives bounded retries after 150, 300 and 600 ms,
  always with the same camera constraints. A persistent busy source produces an
  actionable message and leaves the controls available for another attempt.
  Permission denial does not trigger retries.

## Verification

`node QuicPunchTests/Tests/CameraBrowserTests.mjs` passed in Chromium for desktop
and Android layouts using canvas-generated video, without accessing physical
cameras or microphones. It checks selection before acquisition, cancellation,
repeated start/stop, a temporarily releasing driver, bounded busy failure and
subsequent recovery, delayed/duplicate stop events, device switching, encoding,
preview, call termination while permission is pending, and cleanup. Picker
screenshots were inspected at 1280×900 and 390×844.

The Chromium build lacks an H.264 decoder, so its existing Android native H.264
preview subcheck was skipped; camera preview and synthetic WebCodecs encoding
were exercised.

`--test-webui-security` passed, including a real HTTP stop request and WebSocket
acknowledgement with matching UUID, plus an empty-body compatibility request.
`--test-screenshare` passed. Windows and Android Debug builds succeeded.

The actual webcam and its Windows driver still require a physical-device check.
