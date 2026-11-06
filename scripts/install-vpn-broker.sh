#!/bin/sh
set -eu
if [ "$(id -u)" != 0 ]; then echo 'Run this installer once as root.' >&2; exit 1; fi
if [ "$#" != 2 ]; then echo 'Usage: install-vpn-broker.sh PUBLISH_DIRECTORY OWNER_UID' >&2; exit 1; fi
owner_uid=$2
case "$owner_uid" in ''|*[!0-9]*) echo 'OWNER_UID must be numeric.' >&2; exit 1;; esac
for tool in systemctl ip install realpath getent find grep cp chown chmod; do
    command -v "$tool" >/dev/null || { echo "Required tool missing: $tool" >&2; exit 1; }
done
source_dir=$(realpath "$1")
getent passwd "$owner_uid" >/dev/null || { echo 'OWNER_UID must identify an existing local account.' >&2; exit 1; }
test -c /dev/net/tun || { echo 'Missing /dev/net/tun. Load the tun kernel module first (modprobe tun).' >&2; exit 1; }
test -f "$source_dir/QuicNet"
case "$source_dir" in /opt|/opt/quicnet-vpn|/opt/quicnet-vpn/*) echo 'Use a separate publish directory.' >&2; exit 1;; esac
for target_dir in /opt/quicnet-vpn /etc/quicnet; do
    if [ -L "$target_dir" ]; then echo 'Installation directories must not be symbolic links.' >&2; exit 1; fi
done
if [ -f /etc/quicnet/vpn-broker.json ] && ! grep -Eq '"owner"[[:space:]]*:[[:space:]]*"'"$owner_uid"'"' /etc/quicnet/vpn-broker.json; then
    echo 'The existing service belongs to a different user. Edit owner in the administrator policy first.' >&2; exit 1
fi
for check_dir in "$source_dir" /opt/quicnet-vpn /etc/quicnet; do
    if [ -e "$check_dir" ] && [ -n "$(find "$check_dir" -type l -print -quit)" ]; then
        echo 'Publish and service directories must not contain symbolic links.' >&2; exit 1
    fi
done
systemctl stop quicnet-vpn-broker.service 2>/dev/null || true
install -d -m 0755 /opt/quicnet-vpn /etc/quicnet /etc/systemd/system
cp -R "$source_dir/." /opt/quicnet-vpn/
chown -R root:root /opt/quicnet-vpn /etc/quicnet
chmod -R go-w /opt/quicnet-vpn /etc/quicnet
chmod 0755 /opt/quicnet-vpn/QuicNet
if [ ! -f /etc/quicnet/vpn-broker.json ]; then
    cat > /etc/quicnet/vpn-broker.json <<EOF
{"owner":"$owner_uid","adapterName":"qp-tun0","allowedAddresses":["10.0.0.0/24"],"allowedRoutes":["10.0.0.0/24"],"minimumPrefixLength":24}
EOF
fi
chmod 0644 /etc/quicnet/vpn-broker.json
cat > /etc/systemd/system/quicnet-vpn-broker.service <<'EOF'
[Unit]
Description=QuicNet VPN adapter broker
After=network.target
[Service]
ExecStart=/opt/quicnet-vpn/QuicNet --vpn-broker /etc/quicnet/vpn-broker.json
WorkingDirectory=/opt/quicnet-vpn
Restart=on-failure
RestartSec=5
RuntimeDirectory=quicnet
RuntimeDirectoryMode=0755
UMask=0077
NoNewPrivileges=true
Environment=PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
CapabilityBoundingSet=CAP_NET_ADMIN CAP_CHOWN CAP_DAC_OVERRIDE CAP_FOWNER
RestrictAddressFamilies=AF_UNIX AF_NETLINK AF_INET AF_INET6
ProtectKernelModules=true
ProtectControlGroups=true
ProtectHome=true
ProtectSystem=strict
ReadWritePaths=/run/quicnet /proc/sys/net/ipv4/ip_forward
[Install]
WantedBy=multi-user.target
EOF
if command -v restorecon >/dev/null; then
    # Fedora/SELinux: apply the administrator's default labels after copying the publication.
    restorecon -R /opt/quicnet-vpn /etc/quicnet /etc/systemd/system/quicnet-vpn-broker.service
fi
systemctl daemon-reload
systemctl enable --now quicnet-vpn-broker.service
echo 'VPN adapter broker installed. Administrator policy: /etc/quicnet/vpn-broker.json'
