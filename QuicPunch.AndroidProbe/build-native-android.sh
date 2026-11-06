#!/usr/bin/env bash
set -euo pipefail

# Run inside Linux/WSL. Supply the patched MsQuic source and Android NDK r27c.
: "${MSQUIC_SOURCE:?Set MSQUIC_SOURCE to the patched MsQuic checkout}"
: "${ANDROID_NDK_HOME:?Set ANDROID_NDK_HOME to Android NDK r27c}"

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_dir="$(cd -- "$script_dir/.." && pwd)"
build_dir="${MSQUIC_ANDROID_BUILD_DIR:-/opt/qp-msquic-android-build}"
output_dir="${MSQUIC_ANDROID_OUTPUT_DIR:-/opt/qp-msquic-android-out}"
toolchain="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake"
clang="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android29-clang"

if [[ ! -f "$toolchain" || ! -x "$clang" ]]; then
    echo "Android NDK r27c toolchain was not found at $ANDROID_NDK_HOME" >&2
    exit 1
fi
if [[ ! -f "$MSQUIC_SOURCE/src/inc/msquic.h" || ! -f "$MSQUIC_SOURCE/submodules/openssl/Configure" ]]; then
    echo "MsQuic source and its OpenSSL submodule are required" >&2
    exit 1
fi
if ! grep -q 'X25519MLKEM768:X25519' "$MSQUIC_SOURCE/src/platform/tls_openssl.c"; then
    echo "Apply QuicPunch.NativeBuilder/msquic-openssl-pqc.patch before building" >&2
    exit 1
fi

# Keep the sender's ACK request within the peer's advertised timer range.
# This is required for Android/Windows sessions with different timer resolution.
if ! grep -q 'ACK_FREQUENCY must respect the receiver' "$MSQUIC_SOURCE/src/core/send.c"; then
    patch --batch --forward -d "$MSQUIC_SOURCE" -p1 < "$repo_dir/QuicPunch.NativeBuilder/msquic-ack-frequency.patch"
fi

cmake -S "$MSQUIC_SOURCE" -B "$build_dir" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$toolchain" \
    -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 \
    -DCMAKE_BUILD_TYPE=Release -DQUIC_TLS_LIB=openssl \
    -DQUIC_BUILD_SHARED=ON -DQUIC_BUILD_TEST=OFF \
    -DQUIC_BUILD_TOOLS=OFF -DQUIC_BUILD_PERF=OFF \
    -DQUIC_OUTPUT_DIR="$output_dir" \
    '-DCMAKE_SHARED_LINKER_FLAGS=-Wl,-z,max-page-size=16384'

PATH="$ANDROID_NDK_HOME/toolchains/llvm/prebuilt/linux-x86_64/bin:$PATH" \
    cmake --build "$build_dir" --target msquic --parallel

openssl_dir="$build_dir/_deps/opensslquic-build/openssl"
"$clang" -shared -fPIC -O2 '-Wl,-z,max-page-size=16384' \
    -I"$MSQUIC_SOURCE/src/inc" -I"$openssl_dir/include" \
    -L"$output_dir" -L"$openssl_dir/lib" \
    -o "$build_dir/libqp_probe.so" "$script_dir/native/qp_probe.c" \
    -lmsquic -lcrypto -llog

install -D "$output_dir/libmsquic.so" "$repo_dir/QuicPunch/runtimes/android-arm64/native/libmsquic.so"
install -D "$build_dir/libqp_probe.so" "$script_dir/native/arm64-v8a/libqp_probe.so"
"$clang" -shared -fPIC -O2 '-Wl,-z,max-page-size=16384' \
    -I"$MSQUIC_SOURCE/src/inc" -I"$openssl_dir/include" \
    -L"$output_dir" -L"$openssl_dir/lib" \
    -o "$build_dir/libqp_transport.so" "$script_dir/native/qp_transport.c" \
    -lmsquic -lcrypto -llog
install -D "$build_dir/libqp_transport.so" "$script_dir/native/arm64-v8a/libqp_transport.so"
echo "Android ARM64 MsQuic and certificate-pinned probe built."
