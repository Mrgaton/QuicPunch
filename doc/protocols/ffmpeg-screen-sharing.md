# FFmpeg screen sharing

The Windows sender can capture and encode screen video in FFmpeg while receivers
continue to use WebCodecs. Receiving video does not require FFmpeg. The existing
native Opus microphone pipeline is independent of this feature.

## Setup and selection

FFmpeg is installed separately and is not embedded in Native AOT or Android releases. The media studio detects a usable installation, shows platform-specific installation commands when absent, and can detect it again without restarting. Windows recommends yt-dlp.FFmpeg via WinGet; Gyan.FFmpeg is an alternative. Linux instructions use os-release for Debian/Ubuntu, Fedora and Arch families; the current native capture backend still requires Windows. Android never requests FFmpeg. The binary
must include the desired NVENC/AMD AMF or CPU encoder and the capture/filter support.
Driver and hardware availability are verified with an actual synthetic encode,
not inferred from `ffmpeg -encoders` alone. The selected resolution and settings
are verified again when starting capture. Probe results are cached until restart
or an explicit **Volver a detectar** request in the media studio.

Open **Audio y vídeo** from Calls or Settings. The same studio contains audio
devices, Opus quality, codec selection and encoder tuning. The screen/window gallery
opens only when starting a share. Applying encoder changes to an active FFmpeg share
keeps its confirmed source, including after reopening the settings. Installation
instructions disappear when FFmpeg is available; there is no installed banner.
Controls show a concrete preselected engine and encoder, without an Automatic option. Formats are grouped by GPU first and CPU second. The separate encoder field names the implementation (for example NVIDIA NVENC, AMD AMF, or libx264); it does not select another video format. Native tuning controls are disabled on the browser route. Explicit VBR quality is available for NVENC, libx264 and libx265.

`GET /api/voice/media-capabilities` returns active backend display modes, source
dimensions, supported application frame-rate targets and native encoder probes.
The old `screen-share/gpu-codecs` endpoint remains an alias. `?refresh=1` reruns
the synthetic native encoder probes. Display inventory is refreshed on each studio opening.
Browser capabilities are probed in the actual browser, because a phone opening a
desktop backend must not inherit that desktop's WebCodecs support. The diagnostic table shows the selected engine only, alongside local browser decoding for H.264, HEVC, VP9 and AV1. FFmpeg options are derived solely from native probes and never await browser encoder probing. Browser probes also try `no-preference` when hardware/software preferences both fail, labelling that result separately without claiming GPU or CPU identification. WebCodecs verifies an actual synthetic encode at the selected geometry/FPS in addition to querying support. Native encoding is tested at 320×180; the selected profile is
still verified at startup. WebCodecs support does not prove GPU encoding:
[`hardwareAcceleration` is a hint](https://www.w3.org/TR/webcodecs/#hardware-acceleration).

Resolution presets are bounded by the chosen source. Windows active display
modes use physical pixels, not browser `window.screen` dimensions. With multiple
monitors DXGI output 0 cannot be mapped reliably through Win32 display order:
its preliminary bound is explicitly labelled as an estimate from the largest
display. Choose a named monitor for exact dimensions. Actual native scaling
never enlarges the source. A browser source's original dimensions are discovered
only after the user chooses it; before that, only Original is offered. Capture
size and aspect ratio are preserved, including portrait and ultrawide sources.
The 144 FPS option is an application target, not a detected maximum monitor rate
or a promise of measured throughput.

Initial selection prefers an available H.264 GPU encoder for compatibility, then a tested CPU encoder, then the browser. A saved explicit selection is preserved when available; an unavailable saved preference is visibly replaced with a tested concrete choice. Unknown or incompatible receiver announcements are shown separately and never disable a locally verified encoder in the settings. The exact decoder handshake still runs before any video is transmitted. Starting an explicit FFmpeg or WebCodecs route reports errors for that route; it does not silently change video format.

The settings studio opens the same native source picker directly through **Elegir pantalla o ventana**, replacing the text-only source dropdown. Choosing **Usar esta fuente** returns to settings without starting transmission; starting a share reconfirms the selected source. The native source picker is a searchable gallery of named physical monitors, the full desktop, and visible application windows. Thumbnails and the selected source's periodically refreshed preview use the same input rectangle as transmission. Preview processes are bounded by a two-slot semaphore and a five-second deadline. Captures are local until the user confirms sharing. Windows are addressed by window handle rather than one main window per process; minimized windows are excluded. The primary monitor is preselected only when no source has been selected. Legacy DXGI IDs remain accepted by the encoder but are not shown as ambiguous numbered sources in the picker.

The picker follows the application dark theme, focuses search on opening, and accepts typing from the source gallery or other non-editable controls without an extra click. Arrow keys move to the neighboring source in the actual visual row or column, including after resizing. Enter confirms the visible selected source; Escape cancels and returns focus. A filter with no matches clears the selection and disables confirmation. On narrow displays the selected preview is compact so the source list remains reachable. `ScreenPickerBrowserTests.mjs` exercises these flows with simulated sources and checks text contrast in dark mode without sharing a real screen.

Full desktop, monitor and window capture use GDI input and the selected GPU or CPU encoder. Monitor capture uses physical input bounds before output scaling; a requested 1080p output must not become a top-left 1080p crop of a 1440p source.

Aspect ratio is preserved without upscaling or padding. Video is deliberately 8-bit SDR 4:2:0 for browser
interoperability. HDR and arbitrary pixel formats are not exposed. Native capture
currently supplies video only; system/tab audio continues to require the browser
capture route. The microphone works through the native voice service in both modes.

## Encoder tuning

H.264, HEVC and AV1 have native NVENC and AMF candidates. CPU alternatives are libx264, libx265, libvpx-vp9 and libsvtav1; each must pass a real encode probe. SVT-AV1 CBR uses its low-delay prediction mode. VP9 signals a codec level derived from dimensions and frame rate. Tuning selections are stored per codec in local storage.

| Setting | NVENC | AMD AMF |
| --- | --- | --- |
| Latency | `tune=ull`, zero latency, no lookahead | `usage=ultralowlatency`, no preanalysis |
| Preset speed / balanced / quality | P1 / P3 / P5 | speed / balanced / quality |
| Rate control | CBR or VBR with CQ | CBR or latency constrained VBR |
| B-frames | disabled | disabled |
| Random access | GOP interval, forced IDR | GOP interval, HEVC IDR header insertion |

Bitrate, GOP interval and VBV duration are configurable. The VBV minimum is one
frame's bitrate allocation. GPU-only tuning does not apply to WebCodecs fallback.
Runtime versions that reject options trigger the fallback with an explicit error.

The optional `EncoderPreset` overrides the general priority with an encoder-specific
whitelisted value: NVENC P1–P7, x264/x265 ultrafast through slow, VP9 cpu-used 8–4,
SVT-AV1 12–6, or AMF speed/balanced/quality. `nativeEncoders[].Presets` advertises
these choices. Automatic retains the priority mappings above; SVT-AV1 maps
speed/balanced/quality to 12/10/8. A manual preset disables the general-priority
control, and incompatible presets reset when selecting another encoder. The
current preset and 8-bit 4:2:0 output profile are shown beside the controls.
Parameter references: [FFmpeg encoder documentation](https://ffmpeg.org/ffmpeg-codecs.html#Video-Encoders)
and the installed encoder's `ffmpeg -h encoder=NAME` output.

Local WebCodecs decoding has a separately persisted automatic, prefer-GPU or
prefer-CPU setting. It applies to received streams and the native local preview,
without changing other participants' decoding or restarting capture. Active
stream configurations are checked before accepting a change; decoders reconfigure
on their next keyframe. These are [hardware acceleration preferences](https://developer.mozilla.org/en-US/docs/Web/API/VideoDecoder/configure),
not a measurement of which hardware the runtime actually uses.

`FfmpegTuningBrowserTests.mjs` covers preset persistence, source confirmation,
reconfiguration without changing the source, decoder preference and rejection,
and narrow layouts. `--test-ffmpeg-screen` validates preset arguments and rejects
invalid values, with a real synthetic x264 medium-preset encode.

## Wire format and decoding

No container travels over the network. Matroska is used locally to extract H.264
and HEVC packet boundaries, random access flags, timestamps and codec private
data. AV1 uses IVF plus OBU parsing because some FFmpeg builds cannot update AV1
codec private data on an unseekable Matroska output. AV1 sequence headers are
retained and repeated at random access frames for receiver recovery and joining.

The existing video payload remains `[flags][metadata length][JSON][encoded frame]`.
Metadata includes the codec string (`c`), optional base64 decoder description (`d`),
presentation timestamp in microseconds (`t`), producer generation (`g`), width
(`w`) and height (`h`). Existing peers can ignore the additional JSON fields.
H.264/HEVC descriptions use avcC/hvcC and samples are length prefixed accordingly.
AV1 samples contain low-overhead OBUs and the codec string comes from the sequence
header. Codec/profile/level are read from the actual encoder output.

New control messages 0x27/0x28 request and return WebCodecs support for the exact
decoder configuration. Native streaming starts after viewers approve it. New
viewers are checked before they receive native video. Older peers that cannot
answer the check are not assumed compatible. Web fallback also verifies its
codec string and dimensions with viewers before announcing the stream.

The sender drains FFmpeg continuously and queues at most two frames. Overflow or
stale output drops dependent frames until a fresh keyframe. Browser encoder and
decoder queues are also bounded; reset is followed by configure, stale producer
generations are ignored, and decoder configuration changes wait for a keyframe.
This limits application buffering; the reliable P2P stream can still incur network
head-of-line blocking. Video/audio synchronization across the screen audio path
has not been implemented by this change.

## Platforms and verification

Desktop and Android projects compile with the shared decoder/probe changes.
Android native screen capture is still a separate MediaProjection/MediaCodec
integration; the Windows FFmpeg CLI is not used on Android. Web decoding depends
on the codecs exposed by the device's WebView and is negotiated at runtime.

Run:

```powershell
dotnet run --project QuicPunchTests -c CodexVerification -- --test-ffmpeg-screen
node QuicPunchTests/Tests/ScreenVideoWebTests.mjs
dotnet run --project QuicPunchTests -c CodexVerification -- --test-webui-security
```

The FFmpeg tests encode synthetic H.264/HEVC/AV1 samples, exercise fragmented pipe
reads, validate timestamps/key flags/configuration, verify GPU candidates actually
available on the machine, and exercise capture lifecycle with a synthetic source.
The JavaScript tests validate decoder recovery and configuration changes with an
API mock. `ScreenVideoBrowserTests.html` additionally tests the production decoder
in a real browser using exported synthetic fixtures. Set
`QUICNET_SCREEN_TEST_FIXTURES` to the fixture JSON output path before running the
FFmpeg test, then serve the HTML alongside `fixtures.json` and production
`voice.js` on localhost. Browser decoding and GPU encoding are separate checks;
neither establishes a full two-device call or Android device performance.

## Browser frame pacing and scaling

WebCodecs retains the latest source image and samples it on an independent fixed cadence. Static or irregular source updates therefore do not lower the requested encoder input rate; the encoder may still drop ticks when its bounded queue is busy. Timestamps are monotonic, retained frames are closed on replacement/stop, and late frames from an old generation are discarded. Actual sent FPS and bitrate are displayed separately from the target. Any required downscale draws the complete source image into the output canvas, preserves aspect ratio, rounds to even dimensions, and never enlarges the source. Capture constraints are treated as requests: the encoded output still respects the selected limit when a browser ignores them.
