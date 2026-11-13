#!/usr/bin/env bash
# Run as root in a Linux/WSL test host with iproute2 and tayga installed.
# Arguments: native probe executable, identity directory (outside the repository).
set -euo pipefail
probe=$(realpath "$1")
identities=$(realpath "$2")
prefix="qp64-$$"
client="$prefix-client"; router="$prefix-router"; server="$prefix-server"
state=$(mktemp -d)
cleanup() {
  result=$?
  if [ "$result" -ne 0 ]; then cat "$state/server.log" "$state/tayga.log" 2>/dev/null || true; fi
  for ns in "$client" "$router" "$server"; do
    ip netns pids "$ns" 2>/dev/null | xargs -r kill 2>/dev/null || true
    ip netns delete "$ns" 2>/dev/null || true
  done
  rm -f "$state/tayga.conf" "$state/tayga.log" "$state/dynamic.map" "$state/server.log"
  rmdir "$state" 2>/dev/null || true
}
trap cleanup EXIT
for ns in "$client" "$router" "$server"; do ip netns add "$ns"; ip -n "$ns" link set lo up; done
ip link add qp64c type veth peer name qp64rc
ip link set qp64c netns "$client"; ip link set qp64rc netns "$router"
ip link add qp64s type veth peer name qp64rs
ip link set qp64s netns "$server"; ip link set qp64rs netns "$router"
ip -n "$client" addr add 2001:db8:64::2/64 dev qp64c nodad
ip -n "$router" addr add 2001:db8:64::1/64 dev qp64rc nodad
ip -n "$server" addr add 192.0.2.2/24 dev qp64s
ip -n "$router" addr add 192.0.2.1/24 dev qp64rs
ip -n "$client" link set qp64c mtu 1280 up
ip -n "$router" link set qp64rc mtu 1280 up
ip -n "$server" link set qp64s up; ip -n "$router" link set qp64rs up
ip -n "$client" -6 route add default via 2001:db8:64::1
ip -n "$server" route add default via 192.0.2.1
ip netns exec "$server" sysctl -qw net.ipv6.conf.all.disable_ipv6=1
ip netns exec "$router" sysctl -qw net.ipv4.ip_forward=1 net.ipv6.conf.all.forwarding=1
cat > "$state/tayga.conf" <<EOF
tun-device nat64
ipv4-addr 192.0.2.254
ipv6-addr 2001:db8:64::ffff
prefix 2001:db8:64:64::/96
dynamic-pool 192.168.255.0/24
data-dir $state
EOF
ip netns exec "$router" tayga -c "$state/tayga.conf" --mktun
ip -n "$router" link set nat64 up
ip -n "$router" addr add 192.0.2.254/32 dev nat64
ip -n "$router" route add 192.168.255.0/24 dev nat64
ip -n "$router" -6 route add 2001:db8:64:64::/96 dev nat64
ip netns exec "$router" tayga -c "$state/tayga.conf" --nodetach > "$state/tayga.log" 2>&1 &
echo 'TOPOLOGY: IPv6-only client (MTU 1280) -> TAYGA NAT64 -> IPv4-only server'
ip -n "$client" -br addr
ip -n "$server" -br addr
for mode in baseline delayed; do
  if [ "$mode" = delayed ]; then
    ip netns exec "$client" tc qdisc add dev qp64c root netem delay 25ms 5ms loss 1%
    ip netns exec "$server" tc qdisc add dev qp64s root netem delay 25ms 5ms
  fi
  echo "CASE: $mode"
  ip netns exec "$server" "$probe" server "$identities/server.pfx" "$identities/client.der" 0 44777 0 > "$state/server.log" 2>&1 &
  server_pid=$!
  for unused in $(seq 1 50); do grep -q READY "$state/server.log" && break; sleep .1; done
  ip netns exec "$client" "$probe" client "$identities/client.pfx" "$identities/server.der" 2001:db8:64:64::c000:202 44777 44888
  wait "$server_pid"
  cat "$state/server.log"
done
echo 'PASS actual NAT64 translation, pinned hybrid TLS, BBR, streams, FIN and bidirectional datagrams; no TURN'
