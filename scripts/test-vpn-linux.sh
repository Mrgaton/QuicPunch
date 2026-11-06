#!/bin/sh
# Real kernel tests run in private namespaces: no host interfaces, routes, sockets or sysctls are changed.
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
dotnet_path=$(command -v dotnet)
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_CLI_USE_MSBUILD_SERVER=0
dotnet build "$repo/QuicPunch.Vpn.Tests" -v quiet
# The broker test launches nobody; a checkout inside a private home directory need not be readable by it.
work=$(mktemp -d /tmp/quicnet-vpn-tests.XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT HUP INT TERM
cp -R "$repo/QuicPunch.Vpn.Tests/bin/Debug/net11.0/." "$work/"
chmod -R a+rX "$work"
assembly="$work/QuicPunch.Vpn.Tests.dll"
"$dotnet_path" "$assembly"
if [ "$(id -u)" = 0 ]; then privilege=''; else privilege='sudo'; fi
$privilege unshare --net --mount --propagation private sh -eu -c '
    mount -t tmpfs -o mode=0755 tmpfs /run
    ip link set lo up
    export QUICNET_VPN_TEST_ISOLATED=1
    exec "$1" "$2" --integration
' sh "$dotnet_path" "$assembly"
