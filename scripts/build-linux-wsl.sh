#!/usr/bin/env bash
set -euo pipefail
repo="$1"; version="$2"; build="$3"; channel="$4"; output="$5"
cache="${XDG_CACHE_HOME:-$HOME/.cache}/QuicNet"
work="$cache/release-builds/$version-$build-$channel"
mkdir -p "$work" "$output"
# Keep source fresh for each publish. Reusing an extracted tree also reuses deleted files.
run="$(mktemp -d "$work/build.XXXXXX")"
trap 'rm -rf -- "$run"' EXIT
mkdir -p "$run/source"
# Build on the Linux filesystem: NTFS/network mounts are slow for compiler trees.
tar -C "$repo" --exclude=bin --exclude=obj --exclude='.codex-*' --exclude=.git --exclude=.artifacts --exclude='*.pdb' --exclude='*.dbg' -cf - \
  QuicNet QuicNet.Shared QuicNet.Geolocation QuicPunch QuicPunch.Vpn QuicPunch.NativeBuilder QuicPunch.AndroidProbe/native QuicNet.ReleaseTools native/media Release.props QuicPunch.slnx scripts | tar -C "$run/source" -xf -
media_rel=QuicNet.Shared/bin/native/linux-x64/libnettt_media.so
if [[ -f "$repo/$media_rel" ]]; then
    mkdir -p "$run/source/${media_rel%/*}"
    cp "$repo/$media_rel" "$run/source/$media_rel"
fi
export QUICNET_NATIVE_BUILD_CACHE="$cache/native-builder"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
pwsh -NoProfile -File "$run/source/scripts/build-desktop-release.ps1" -Platform linux-x64 -Version "$version" -Build "$build" -Channel "$channel" -OutputDirectory "$run/output"
cp "$run/output/QuicNet" "$run/output/build-info.json" "$output/"
