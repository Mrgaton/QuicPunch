# Native FFmpeg call video

Call video uses FFmpeg on Windows and Android. WebCodecs, browser video decoding,
and decoded pixel transfer through JavaScript are removed. The WebView controls
settings and native surface geometry. The native Opus microphone pipeline is independent.

See [the implemented architecture](../design/nettt-call-studio/architecture.md)
for the interface, capability semantics, buffer policy and platform limitations.

## Distribution and capture

Windows embeds shared FFmpeg decoding libraries and dav1d in the Native AOT
executable. Reception requires no external FFmpeg installation. Windows screen
and camera encoding uses a separately installed FFmpeg executable, detected by
`FfmpegInstallation`; the interface provides installation guidance when needed.
GPU encoder candidates must pass a synthetic encode, including usable output
configuration, before being advertised. The selected settings are checked again
when capture starts. Available implementations include NVENC, AMF and CPU codecs.

Android bundles ARM64 FFmpeg libraries. MediaProjection or Camera2 supplies an
input Surface; FFmpeg owns the MediaCodec encoder. Component selection is explicit.
The Java MediaCodec API creates the persistent input surface; it does not encode
or decode frames outside FFmpeg. Android disables controls that its adapter does
not apply, including CPU encoder presets, CRF and sender VBV buffer selection.
FPS settings are targets; device camera modes and encoder capacity constrain them.

`GET /api/voice/media-capabilities` returns capture availability, concrete native
encoders, display/source dimensions and application FPS/resolution choices.
`?refresh=1` repeats detection. `/api/voice/cameras` lists native camera sources.
Starting a screen or camera capture requires deliberate source selection and the
platform permission where applicable. Cancelling a pending start also stops any
capture that completes late.

Windows desktop/monitor/window capture uses GDI input with physical source bounds
before scaling. Output preserves aspect ratio without upscaling or padding.
Encoded output is 8-bit SDR 4:2:0. System/tab audio capture is not implemented by
this native video migration; microphone audio remains available.

## Interface

The call studio offers codec, available encoder, encoder preset, resolution, FPS,
bitrate, CBR/VBR, keyframe interval and applicable encoder-specific settings.
The chosen codec is not silently replaced because a recipient cannot decode it.
Controls unavailable on the selected backend are disabled.

Reception is configured independently: automatic / hardware only / software only,
0–250 ms playout buffer and 2–32 queued compressed units. Game and film presets
use 0 ms / 4 units and 80 ms / 16 units respectively. Local preview bypasses the
playout delay. This is not complete audiovisual synchronization.

The compatibility matrix distinguishes verified hardware, verified software,
unsupported and pending per participant and codec. A baseline result is supported
by real decoded synthetic frames at the displayed test dimensions, not an API
preference. It does not certify arbitrary profiles, resolution or FPS. Remote
results are peer announcements. Peer requests read the cached codec inventory without allocating decoder surfaces.
The final profile and effective playback are validated by the first real frames.
Concurrent call joins share one detection task; only explicit refresh repeats it.

Native HWND/SurfaceView presentation is outside the WebView. Header controls occupy
a separate rectangle, including fullscreen, and remain accessible without native
mouse/hover forwarding. Dialogs hide video surfaces; ancestor clipping and device
pixel ratio are included in geometry updates. Hidden streams release decoding
resources and wait for a new keyframe when visible again.

## Protocol

No container travels over the network. Windows uses local Matroska/IVF parsers to
extract access units. Android receives packets from FFmpeg's encoder and retains
AVC/HEVC parameter sets or AV1 sequence headers for late joins.

Video payload: `[flags:1][metadata length:2 big endian][JSON][encoded access unit]`.
Metadata fields are codec string `c`, optional base64 configuration `d`, PTS in
microseconds `t`, producer generation `g`, width `w` and height `h`. The low flag
bit marks random access and bit 1 indicates metadata. Codec IDs are H264=1,
VP9=3, AV1=4 and HEVC=5. Windows AVC/HEVC use avcC/hvcC and corresponding length
prefixes; Android uses Annex B configuration and packets. FFmpeg accepts both.
AV1 uses low-overhead OBUs.

Control frames 0x27/0x28 request and reply to an exact decoder configuration
query, including cached diagnostic mode/reason. This is codec availability, not
a claim that the requested profile was decoded. Frame 0x2B announces native capabilities:
engine availability, supported/hardware codec masks and individual codec evidence.
The existing codec-mask announcement remains for older peers. Missing native
announcements are pending, never assumed to prove hardware support.

Negative/unknown profile checks are visible and do not change the sender's codec.
The existing per-peer gating skips undecodable video while announcements and voice
continue. Runtime automatic decoding can retry software on the next keyframe
following a hardware failure; hardware-only mode reports failure instead.

Each receiver owns its FFmpeg decoder and presentation context on one worker.
Queue overflow clears dependent compressed units and waits for a keyframe. The
queue also has a 16 MiB ceiling and a 2 MiB access-unit limit. Stale generations
are ignored. Decoded intermediate frames may be discarded before presentation.
The reliable transport can still incur head-of-line latency; the measured decoder
processing time is not an end-to-end latency measurement.

## Build and verification

`bash scripts/build-native-media.sh win-x64` or `android-arm64` cross-compiles the
pinned libraries. `--bridge-only` rebuilds the C++ bridge against an existing build.
`scripts/package-native-media-source.sh` prepares corresponding upstream sources,
patches, build instructions and redistribution licenses. The release publisher
includes that archive and verifies the existing signing identities.

Run on the PC:

```powershell
python native/media/test_decoder.py --render
dotnet run --project QuicPunchTests -c Release -- --test-native-video
dotnet run --project QuicPunchTests -c Release -- --test-platform-video
dotnet run --project QuicPunchTests -c Release -- --test-ffmpeg-screen
node scripts/test-web.mjs --web
node scripts/test-web.mjs --browser
```

The C ABI tests decode real samples and exercise native Windows presentation.
Managed tests cover all four software codecs, hardware evidence, buffering,
hidden/stop lifecycle, malformed input and native capture packetization roundtrips.
Browser suites test controls and geometry against simulated native APIs; they do
not prove native rendering. The isolated AOT release must decode all four codecs
using only its bundled libraries. Android package validation checks native
libraries and release signing; final device execution requires an authorized
Android device. Do not use the user's Quest devices.
