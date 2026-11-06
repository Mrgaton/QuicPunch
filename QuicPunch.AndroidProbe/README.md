# QuicPunch for Android

This Android ARM64 app serves the shared `QuicNet.Shared/wwwroot` interface from a loopback HTTP server inside Android WebView. Peer discovery, UDP hole punching, the shared session multiplexer, chat, speed test and voice use the same QuicPunch core as desktop. Android's physical QUIC tunnel uses `native/qp_transport.c` and `AndroidMsQuicTransport.cs`, which call MsQuic directly and pin the peer during TLS by the SHA3-256 of its public key, the same identity (`PeerInfo.CertHash`) desktop validates, so peers known only by hash can connect. Streams and RFC 9221 datagrams are both implemented. The separate `qp_probe` PING/PONG diagnostic remains available.

## Build

Requirements: .NET 11 Android workload, Android SDK API 35 or newer, Android NDK r27c, CMake, Ninja, and the patched MsQuic source with initialized OpenSSL submodule. The build script applies `QuicPunch.NativeBuilder/msquic-ack-frequency.patch` idempotently so outgoing ACK_FREQUENCY frames respect the receiver's advertised minimum timer interval. The MsQuic source must include `QuicPunch.NativeBuilder/msquic-openssl-pqc.patch` and should be kept in a short Linux path (for example `/opt/qp-msquic-android`).

Inside WSL/Linux:

```bash
export MSQUIC_SOURCE=/opt/qp-msquic-android
export ANDROID_NDK_HOME=/opt/android/android-ndk-r27c
./QuicPunch.AndroidProbe/build-native-android.sh
```

On Windows, set `JAVA_HOME` to JDK 21 and point MSBuild to the Android SDK if necessary:

```powershell
./QuicPunch.AndroidProbe/prepare-tor-android.ps1
dotnet build QuicPunch.AndroidProbe/QuicPunch.AndroidProbe.csproj -c Debug -p:AndroidSdkDirectory="$env:LOCALAPPDATA/Android/Sdk" -p:JavaSdkDirectory="$env:JAVA_HOME"
dotnet build QuicPunch.AndroidProbe.Desktop/QuicPunch.AndroidProbe.Desktop.csproj
dotnet run --project QuicPunch.AndroidProbe.Desktop -- server 44555
```

Tor uses Guardian Project's [Tor Android](https://github.com/guardianproject/tor-android)
0.4.9.11 ARM64 executable. The preparation script verifies the pinned archive SHA-256.
The APK extracts `libtor.so` into Android's native library directory and executes it
there, since Android blocks execution from writable app data. Tor data, cookie
authentication and onion identity stay in private app storage. Saved Tor enablement
and Tor Nostr discovery settings are restored on startup. This package supports
direct Tor connections; Snowflake/obfs4 helpers are not included.

Install `QuicPunch.AndroidProbe/bin/Debug/net11.0-android37.0/org.quicpunch.androidprobe-Signed.apk` on an ARM64 Android device. The build copies the complete ARM64 APK to this top-level path. Always rebuild before copying the APK to another device. Grant camera, microphone and notification permissions when prompted. The foreground connection service keeps the node and voice capture running when the activity goes into the background; the notification offers **Desconectar**. Closing the task or using that action stops the service. For the separate diagnostic, **Prueba QUIC** connects to the desktop probe server using its LAN IPv4, UDP port and printed SHA-256 certificate fingerprint.

For a manual WebRTC call, open **Llamada rápida** in the Android app and `/mobile.html` in the desktop app. One side creates an invitation and sends its text to the other side; the other side pastes it, creates a response and sends that back; the first side pastes the response and applies it. WebRTC currently uses local ICE candidates, so both devices should be on the same LAN. The WebRTC call is independent of the QuicPunch QUIC peer session.

## Verification status

`System.Net.Quic` on the tested .NET 11 Android runtime loads MsQuic but fails during certificate validation because `libSystem.Security.Cryptography.Native.OpenSsl.so` is absent. The Android peer tunnel bypasses that runtime path. A Linux build of the same native bridge passed TLS, exact certificate pinning (including rejection of a different certificate), bidirectional streams, simultaneous inbound/outbound streams, datagrams, endpoint lookup and telemetry.

On 2026-09-30, the complete Android-to-desktop QuicPunch session was verified on a OnePlus CPH2769 (API 36): direct chat in both directions, a short bidirectional speed test and a voice session using QUIC datagrams. The user confirmed audible voice. TLS telemetry and the startup self-test confirmed `X25519MLKEM768` (group 4588), with `TLS_AES_256_GCM_SHA384`. Peer identity certificates remain ECDSA P-256. Background operation was checked with the foreground service; prolonged Doze and WAN roaming remain unverified. See [the verification record](../doc/android/verification-2026-09-30.md).

Audio uses 20ms Opus frames. The shared incoming backlog is capped at three frames (60ms), and Windows capture/playback queues have also been reduced. On this phone the active Android playback buffer was reduced from approximately 120ms to 40ms, confirmed by `[ANDROID AUDIO]` in logcat. This is a buffer measurement, not an end-to-end voice latency measurement; the phone reports performance mode `None` despite the low-latency request.

The Debug app runs a native MsQuic loopback check on startup and writes `[ANDROID QUIC SELFTEST]` to logcat. On a device, confirm that this passes, then establish a peer session with the desktop and check for `[SUCCESS] QUIC connection established`. Verify chat, speed test and a voice call over that session. If UDP reports `AccessDenied`, inspect the phone's active VPN or per-app network restrictions; a previous test phone had a `vgate0` VPN interface.

On the tested OnePlus 9, Android WebView captured camera and microphone and generated a WebRTC offer with both audio and video after `MODIFY_AUDIO_SETTINGS` was added to the Android manifest. If either media source is unavailable, the page falls back to the one that works. A complete call with a second WebRTC participant has not yet been verified. The VPN is intentionally absent.
