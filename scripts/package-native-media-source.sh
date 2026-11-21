#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
cache="${QUICNET_MEDIA_CACHE:-$HOME/.cache/nettt-media}"
destination="$repo/.artifacts/native-media-source"
mkdir -p "$destination/scripts" "$destination/native/media"
git -C "$cache/ffmpeg" archive --format=tar --prefix=ffmpeg/ HEAD > "$destination/ffmpeg.tar"
git -C "$cache/dav1d" archive --format=tar --prefix=dav1d/ HEAD > "$destination/dav1d.tar"
git -C "$cache/nv-codec-headers" archive --format=tar --prefix=nv-codec-headers/ HEAD > "$destination/nv-codec-headers.tar"
cp "$repo/scripts/build-native-media.sh" "$repo/scripts/package-native-media-source.sh" "$destination/scripts/"
cp "$repo/native/media/"*.cpp "$repo/native/media/patch_ffmpeg.py" "$repo/native/media/NOTICE.txt" "$destination/native/media/"
cp -r "$repo/native/media/licenses" "$destination/native/media/"
cat > "$destination/README.txt" <<'EOF'
Extract ffmpeg.tar, dav1d.tar and nv-codec-headers.tar for the complete pinned upstream source trees.
native/media/patch_ffmpeg.py contains the Android decoder and Windows DXGI polling patches.
scripts/build-native-media.sh documents the complete cross-compilation commands,
versions, dependency selection and platform flags. It also verifies source commits.
Run that script from the repository root for win-x64, android-arm64 or linux-x64.
Use MinGW for Windows, the Android NDK for Android, or the distribution's FFmpeg,
X11 and Xext development libraries for Linux. Source checkouts are cached
outside the application. NOTICE.txt explains replacement of the shared
libraries in distributed applications. No publication keys are included or needed
to build the libraries.
EOF
tar -czf "$repo/.artifacts/native-media-source.tar.gz" -C "$destination" .
