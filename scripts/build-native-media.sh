#!/usr/bin/env bash
set -euo pipefail
# Run in Linux/WSL. All downloads/builds are reproducible caches, never secrets.
repo="$(cd "$(dirname "$0")/.." && pwd)"
cache="${QUICNET_MEDIA_CACHE:-$HOME/.cache/nettt-media}"
platform="${1:-android-arm64}"
if [[ "$platform" == linux-x64 ]]; then
    destination="${QUICNET_MEDIA_OUTPUT:-$repo/QuicNet.Shared/bin/native/linux-x64}"
    mkdir -p "$destination"
    # Use the distribution's FFmpeg and X11 ABI for this desktop build.
    flags=()
    read -r -a flags <<< "$(pkg-config --cflags --libs libavcodec libavutil libswscale x11 xext)"
    "${CXX:-g++}" -std=c++17 -O2 -fPIC -shared -pthread "$repo/native/media/nettt_media.cpp" "${flags[@]}" -o "$destination/libnettt_media.so"
    echo "Native FFmpeg media built for $platform"
    exit 0
fi
ffmpeg_commit=1041abdc962f4cc4f394aa8de9dc5236c0c3b9e7
dav1d_commit=42b2b24fb8819f1ed3643aa9cf2a62f03868e3aa
mkdir -p "$cache"
checkout() {
    local url="$1" name="$2" tag="$3" commit="$4"
    if [[ ! -d "$cache/$name/.git" ]]; then git clone --depth 1 --branch "$tag" "$url" "$cache/$name"; fi
    [[ "$(git -C "$cache/$name" rev-parse HEAD)" == "$commit" ]] || { echo "Unexpected $name commit"; exit 1; }
}
checkout https://github.com/FFmpeg/FFmpeg.git ffmpeg n8.1.3 "$ffmpeg_commit"
checkout https://code.videolan.org/videolan/dav1d.git dav1d 1.5.1 "$dav1d_commit"
python3 "$repo/native/media/patch_ffmpeg.py" "$cache/ffmpeg"
prefix="$cache/$platform/install"
build="$cache/$platform"
mkdir -p "$prefix" "$build"
common=(--prefix="$prefix" --pkg-config=pkg-config --disable-programs --disable-doc --disable-debug --disable-autodetect --disable-everything --enable-avcodec --enable-avutil --enable-swscale --disable-avformat --disable-avfilter --disable-avdevice --disable-swresample --enable-decoder=h264,hevc,vp9,libdav1d --enable-parser=h264,hevc,vp9,av1 --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,extract_extradata,av1_metadata --enable-libdav1d --enable-shared --disable-static --enable-pic --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib")
case "$platform" in
android-arm64|android-x64)
    ndk="${ANDROID_NDK_HOME:-/opt/android/android-ndk-r27c}"
    toolchain="$ndk/toolchains/llvm/prebuilt/linux-x86_64/bin"
    export PATH="$toolchain:$PATH"
    if [[ "$platform" == android-x64 ]]; then
        cpu=x86_64; abi=x86_64
    else
        cpu=aarch64; abi=arm64-v8a
    fi
    cc="$cpu-linux-android29-clang"; cxx="$cpu-linux-android29-clang++"
    ar=llvm-ar; strip=llvm-strip
    system=android
    ffextra=(--target-os=android --arch="$cpu" --enable-cross-compile --cc="$cc" --cxx="$cxx" --ar="$ar" --ranlib=llvm-ranlib --strip="$strip" --enable-jni --enable-mediacodec --enable-decoder=h264_mediacodec,hevc_mediacodec,vp9_mediacodec,av1_mediacodec --enable-encoder=h264_mediacodec,hevc_mediacodec,av1_mediacodec --extra-ldflags="-L$prefix/lib -Wl,-z,max-page-size=16384")
    destination="$repo/QuicPunch.AndroidProbe/native/$abi"
    if [[ "$platform" == android-x64 ]]; then destination="$repo/QuicPunch.AndroidProbe/bin/native/$abi"; fi
    ;;
win-x64)
    checkout https://github.com/FFmpeg/nv-codec-headers.git nv-codec-headers n12.2.72.0 c69278340ab1d5559c7d7bf0edf615dc33ddbba7
    make -C "$cache/nv-codec-headers" install PREFIX="$prefix" LIBDIR=lib
    cc=x86_64-w64-mingw32-gcc-posix; cxx=x86_64-w64-mingw32-g++-posix
    ar=x86_64-w64-mingw32-ar; strip=x86_64-w64-mingw32-strip
    system=windows; cpu=x86_64
    ffextra=(--target-os=mingw32 --arch=x86_64 --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- --cc="$cc" --cxx="$cxx" --enable-d3d11va --enable-dxva2 --enable-hwaccel=h264_d3d11va,h264_d3d11va2,hevc_d3d11va,hevc_d3d11va2,vp9_d3d11va,vp9_d3d11va2,av1_d3d11va,av1_d3d11va2 --enable-decoder=av1 --extra-ldflags="-L$prefix/lib -static-libgcc")
    ffextra+=(--enable-ffnvcodec --enable-nvenc --enable-encoder=h264_nvenc,hevc_nvenc --enable-avfilter --enable-filter=ddagrab)
    destination="$repo/QuicNet.Shared/runtimes/win-x64/native"
    ;;
*) echo "Unsupported platform: $platform"; exit 1;;
esac
destination="${QUICNET_MEDIA_OUTPUT:-$destination}"
cat > "$build/cross.ini" <<EOF
[binaries]
c = '$cc'
cpp = '$cxx'
ar = '$ar'
strip = '$strip'
pkgconfig = 'pkg-config'
[host_machine]
system = '$system'
cpu_family = '$cpu'
cpu = '$cpu'
endian = 'little'
EOF
if [[ ! -f "$prefix/lib/pkgconfig/dav1d.pc" ]]; then
    meson setup "$build/dav1d" "$cache/dav1d" --cross-file "$build/cross.ini" --prefix "$prefix" --libdir lib --default-library static --buildtype release -Denable_tools=false -Denable_tests=false -Dtestdata_tests=false
    ninja -C "$build/dav1d" install
fi
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
mkdir -p "$build/ffmpeg"
cd "$build/ffmpeg"
if [[ "${2:-}" != --bridge-only ]]; then
    "$cache/ffmpeg/configure" "${common[@]}" "${ffextra[@]}"
    make -j"${QUICNET_MEDIA_JOBS:-8}"
    make install
fi
mkdir -p "$destination"
if [[ "$platform" == win-x64 ]]; then
    "$cxx" -std=c++17 -O2 -shared -static-libgcc -static-libstdc++ -I"$prefix/include" "$repo/native/media/nettt_media.cpp" "$repo/native/media/windows_screen_encoder.cpp" -L"$prefix/lib" -lavfilter -lavcodec -lavutil -lswscale -ld3d11 -ldxgi -luser32 -o "$destination/nettt_media.dll"
    cp "$prefix/bin/"*.dll "$destination/"
    # libgcc is statically linked; POSIX toolchain's synchronization runtime is explicit.
    winpthread="$("$cc" -print-file-name=libwinpthread-1.dll)"
    cp "$winpthread" "$destination/"
else
    "$cxx" -std=c++17 -O2 -fPIC -shared -static-libstdc++ -I"$prefix/include" "$repo/native/media/nettt_media.cpp" -L"$prefix/lib" -lavcodec -lavutil -lswscale -landroid -llog -lEGL -lGLESv2 -Wl,-z,max-page-size=16384 -Wl,-rpath,'$ORIGIN' -o "$destination/libnettt_media.so"
    cp -L "$prefix/lib/"*.so "$destination/"
fi
echo "Native FFmpeg media built for $platform"
