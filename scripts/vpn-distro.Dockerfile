# Only userspace compatibility: containers share their host kernel and do not validate a desktop firewall/LSM policy.
ARG BASE_IMAGE=ubuntu:24.04
FROM ${BASE_IMAGE}
RUN . /etc/os-release && \
    case "$ID" in \
      ubuntu) apt-get update && apt-get install -y --no-install-recommends iproute2 util-linux procps libstdc++6 libssl3 zlib1g && rm -rf /var/lib/apt/lists/* ;; \
      arch) pacman -Syu --noconfirm --needed iproute2 util-linux procps-ng gcc-libs openssl zlib ;; \
      fedora) dnf install -y iproute util-linux procps-ng libstdc++ openssl-libs zlib-ng-compat && dnf clean all ;; \
      *) echo "Unsupported test distribution: $ID" >&2; exit 1 ;; \
    esac
ENV DOTNET_ROOT=/runtime \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    QUICNET_VPN_TEST_ISOLATED=1
COPY install-vpn-broker.sh test-vpn-distro.sh /checks/
ENTRYPOINT ["/bin/sh", "/checks/test-vpn-distro.sh"]
