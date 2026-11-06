#!/bin/sh
# Container entrypoint. Installer writes remain inside the disposable container; systemctl/restorecon are mocked.
set -eu
test "${QUICNET_VPN_TEST_ISOLATED:-}" = 1
cat /etc/os-release
ip -Version
getconf GNU_LIBC_VERSION
mkdir -p /tmp/vpn-installer-tools /tmp/vpn-publication
cat > /tmp/vpn-installer-tools/systemctl <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >> /tmp/vpn-systemctl.log
EOF
cat > /tmp/vpn-installer-tools/restorecon <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >> /tmp/vpn-restorecon.log
EOF
printf '#!/bin/sh\nexit 0\n' > /tmp/vpn-publication/QuicNet
chmod 755 /tmp/vpn-installer-tools/* /tmp/vpn-publication/QuicNet
export PATH="/tmp/vpn-installer-tools:$PATH"
owner=$(id -u nobody)
sh /checks/install-vpn-broker.sh /tmp/vpn-publication "$owner"
test -x /opt/quicnet-vpn/QuicNet
grep -q '"owner":"'"$owner"'"' /etc/quicnet/vpn-broker.json
grep -q '^CapabilityBoundingSet=CAP_NET_ADMIN ' /etc/systemd/system/quicnet-vpn-broker.service
grep -q '^Environment=PATH=.*:/usr/sbin:/usr/bin:' /etc/systemd/system/quicnet-vpn-broker.service
grep -q 'enable --now quicnet-vpn-broker.service' /tmp/vpn-systemctl.log
grep -q '/opt/quicnet-vpn /etc/quicnet /etc/systemd/system/quicnet-vpn-broker.service' /tmp/vpn-restorecon.log
if sh /checks/install-vpn-broker.sh /tmp/vpn-publication 0 > /tmp/vpn-owner-rejected.log 2>&1; then
    echo 'Installer incorrectly replaced another owner.' >&2; exit 1
fi
grep -q 'belongs to a different user' /tmp/vpn-owner-rejected.log
echo '[PASS] Installer files, service settings, SELinux relabel invocation and owner preservation (mocked systemctl/restorecon).'
exec /runtime/dotnet /tests/QuicPunch.Vpn.Tests.dll --integration
