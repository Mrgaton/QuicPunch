#!/bin/sh
# Run on a Linux Docker host. Containers exercise distro userspace; native firewall/SELinux/systemd need VM tests.
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
command -v docker >/dev/null
docker info >/dev/null
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
dotnet build "$repo/QuicPunch.Vpn.Tests" -v quiet
runtime=$(dirname "$(readlink -f "$(command -v dotnet)")")
work=$(mktemp -d /tmp/quicnet-vpn-distros.XXXXXXXX)
image="quicnet-vpn-distros:check-$$"
trap 'docker image rm "$image" >/dev/null 2>&1 || true; rm -rf -- "$work"' EXIT HUP INT TERM
cp -R "$repo/QuicPunch.Vpn.Tests/bin/Debug/net11.0/." "$work/"
chmod -R a+rX "$work"
if [ "$#" = 0 ]; then set -- ubuntu:24.04 archlinux:base fedora:44; fi
for base in "$@"; do
    echo "=== VPN compatibility: $base ==="
    docker build --build-arg "BASE_IMAGE=$base" --tag "$image" --file "$repo/scripts/vpn-distro.Dockerfile" "$repo/scripts"
    # A disposable network namespace is essential: tests install default routes and change ip_forward.
    docker run --rm --privileged --network none --tmpfs /run \
        --mount "type=bind,source=$runtime,target=/runtime,readonly" \
        --mount "type=bind,source=$work,target=/tests,readonly" "$image"
done
