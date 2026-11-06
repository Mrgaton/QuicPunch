#!/usr/bin/env bash
set -euo pipefail

# Runs the same C transport bridge on Linux to validate TLS, certificate
# pinning, streams and datagrams before packaging the Android ARM64 binary.
: "${MSQUIC_SOURCE:?Set MSQUIC_SOURCE to the patched MsQuic checkout}"
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
build_dir="${MSQUIC_LINUX_BUILD_DIR:-$HOME/.cache/quicpunch-msquic-linux}"
output_dir="$build_dir/out"
work_dir="$(mktemp -d)"
trap 'rm -rf -- "$work_dir"' EXIT

cmake -S "$MSQUIC_SOURCE" -B "$build_dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release -DQUIC_TLS_LIB=openssl \
    -DQUIC_BUILD_SHARED=ON -DQUIC_BUILD_TEST=OFF \
    -DQUIC_BUILD_TOOLS=OFF -DQUIC_BUILD_PERF=OFF \
    -DQUIC_OUTPUT_DIR="$output_dir"
cmake --build "$build_dir" --target msquic --parallel

openssl_dir="$build_dir/_deps/opensslquic-build/openssl"
gcc -shared -fPIC -O2 \
    -I"$MSQUIC_SOURCE/src/inc" -I"$openssl_dir/include" \
    -L"$output_dir" -L"$openssl_dir/lib" \
    -o "$work_dir/libqp_transport_host.so" "$script_dir/native/qp_transport.c" \
    -lmsquic -lcrypto -lpthread
gcc -O2 -L"$work_dir" -L"$output_dir" -Wl,-rpath-link,"$output_dir" \
    -o "$work_dir/qp_transport_test" "$script_dir/native/qp_transport_test.c" \
    -lqp_transport_host -lmsquic -lpthread

openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes \
    -keyout "$work_dir/test.key" -out "$work_dir/test.crt" \
    -subj /CN=qp-test -days 1 >/dev/null 2>&1
openssl pkcs12 -export -inkey "$work_dir/test.key" -in "$work_dir/test.crt" \
    -out "$work_dir/test.pfx" -passout pass: >/dev/null 2>&1
openssl x509 -in "$work_dir/test.crt" -outform DER -out "$work_dir/test.der"
export LD_LIBRARY_PATH="$work_dir:$output_dir${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
timeout 15 "$work_dir/qp_transport_test" "$work_dir/test.pfx" "$work_dir/test.der"

openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes \
    -keyout "$work_dir/other.key" -out "$work_dir/other.crt" \
    -subj /CN=qp-other -days 1 >/dev/null 2>&1
openssl x509 -in "$work_dir/other.crt" -outform DER -out "$work_dir/other.der"
openssl pkcs12 -export -inkey "$work_dir/other.key" -in "$work_dir/other.crt" \
    -out "$work_dir/other.pfx" -passout pass: >/dev/null 2>&1

# A handshake from an unexpected identity must not consume the listener.
timeout 20 "$work_dir/qp_transport_test" "$work_dir/test.pfx" "$work_dir/test.der" "$work_dir/other.pfx"

set +e
timeout 8 "$work_dir/qp_transport_test" "$work_dir/test.pfx" "$work_dir/other.der" \
    >"$work_dir/rejected.log" 2>&1
status=$?
set -e
if [[ "$status" -ne 7 ]]; then
    cat "$work_dir/rejected.log" >&2
    echo "Certificate mismatch was not rejected as expected (exit $status)" >&2
    exit 1
fi
echo "PASS rejected different peer certificate"
