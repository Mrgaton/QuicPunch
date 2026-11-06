#!/usr/bin/env bash
set -euo pipefail
repo="$1"; version="$2"; build="$3"; channel="$4"; output="$5"
cache="${XDG_CACHE_HOME:-$HOME/.cache}/QuicNet"
work="$cache/release-builds/$version-$build-$channel"
mkdir -p "$work/source" "$output"
# Build on the Linux filesystem: NTFS/network mounts are slow for compiler trees.
tar -C "$repo" --exclude=bin --exclude=obj --exclude='.codex-*' --exclude=.git --exclude=.artifacts --exclude='*.pdb' --exclude='*.dbg' -cf - \
  QuicNet QuicNet.Shared QuicNet.Geolocation QuicNet.Geolocation.App QuicPunch QuicPunch.Vpn QuicPunch.NativeBuilder QuicNet.ReleaseTools Release.props QuicPunch.slnx scripts | tar -C "$work/source" -xf -
export QUICNET_NATIVE_BUILD_CACHE="$cache/native-builder"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
pwsh -NoProfile -File "$work/source/scripts/build-desktop-release.ps1" -Platform linux-x64 -Version "$version" -Build "$build" -Channel "$channel" -OutputDirectory "$work/output"
cp "$work/output/QuicNet" "$work/output/build-info.json" "$output/"
