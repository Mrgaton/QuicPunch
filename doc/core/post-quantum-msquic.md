# Post-quantum QUIC with OpenSSL-backed MsQuic

QuicPunch can use an application-local `msquic.dll` on Windows x64 or `libmsquic.so` on Linux x64, built with the OpenSSL version pinned by MsQuic. The native patch makes TLS 1.3 offer `X25519MLKEM768` first and keeps `X25519` as the classical fallback. This changes only the TLS key establishment: the existing ECDSA P-256 identity certificates remain unchanged, so this work does not enable ML-DSA authentication.

## Prerequisites

The repository has one cross-platform .NET native-builder project. It validates the host and toolchain, checks out the exact commits from `native-build.lock.json`, builds, verifies, and copies the correct native library.

Native compilers and operating-system SDKs are not checked into this repository. Windows binaries must link to a Windows SDK and Linux binaries must link to the target distribution's libc; copying those SDKs into the source tree is unsupported and may violate their redistribution terms. A local build therefore needs the native toolchain below. The included GitHub Actions matrix supplies it automatically and is the zero-setup way to produce both libraries.

Windows requires Git, CMake, Strawberry Perl, NASM, PowerShell 7, and Visual Studio Build Tools with the Desktop development with C++ workload:

```powershell
winget install --id Kitware.CMake --exact
winget install --id StrawberryPerl.StrawberryPerl --exact
winget install --id NASM.NASM --exact
winget install --id Microsoft.PowerShell --exact
winget install --id Microsoft.VisualStudio.2022.BuildTools --exact --override "--wait --passive --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended"
```

Linux requires Git, CMake, Perl, NASM, PowerShell, and either Clang or GCC with the distribution's libc development package. For example on Ubuntu:

```bash
sudo apt-get install build-essential clang cmake git liblttng-ust-dev libnuma-dev liburing-dev nasm perl
```

PowerShell is preinstalled on GitHub-hosted Ubuntu runners. For local Linux builds, install the package from Microsoft's repository if `pwsh` is not already present.

## Reproduce the native build

Run from the repository root:

```text
dotnet run --project QuicPunch.NativeBuilder -c Release
```

The RID defaults to the current x64 host. It can be explicit with `-- --rid win-x64` or `-- --rid linux-x64`. Native linking is platform-specific, so each RID is built on its matching OS. The `Native MsQuic PQC` workflow builds both from the same project and publishes separate workflow artifacts.

On Windows the temporary checkout lives under the short `NativeBuild/` directory because OpenSSL's Perl-generated build paths can otherwise exceed legacy `MAX_PATH` limits. Linux uses `.artifacts/native-builder/`. Both caches are ignored by git.

The lock file pins MsQuic commit `80a065112426bce68c1da42d026478d3e40fd45e`, whose OpenSSL submodule was `3.5.9-dev` when verified. The builder clones all submodules, applies [`QuicPunch.NativeBuilder/msquic-openssl-pqc.patch`](../../QuicPunch.NativeBuilder/msquic-openssl-pqc.patch), builds the OpenSSL TLS backend, verifies `ML-KEM-768` and `X25519MLKEM768`, checks that OpenSSL is statically linked into MsQuic, and copies the result under `QuicPunch/runtimes/<rid>/native/`.

Both generated libraries are deliberately ignored by git. Rebuild them locally or in a trusted release pipeline; do not commit them.

## Loading behavior

`MsQuicNativeLoader` runs from the `QuicPunchConnection` static constructor. On Windows, when the local DLL exists, it enables the runtime switch `System.Net.Quic.AppLocalMsQuic` and loads the absolute application-local path before `System.Net.Quic` initializes. The switch is required on current .NET runtimes because Windows otherwise loads the MsQuic copy beside `System.Net.Quic.dll`, even if another DLL with the same name has already been preloaded.

On Linux the loader looks for `libmsquic.so` in the application directory and otherwise does nothing. Other platforms are no-ops.

## Build and handshake verification

```powershell
dotnet build .\QuicPunch.slnx -c Release
.\QuicPunchTests\bin\Release\net11.0\QuicPunchTests.exe --test-pqc-handshake
```

The handshake test checks that only the application-local `msquic.dll` is loaded and reads `QUIC_HANDSHAKE_INFO.TlsGroup` from the client connection. Success reports IANA TLS NamedGroup `4588`, `X25519MLKEM768`. A single endpoint is sufficient to identify the shared TLS group; MsQuic may release the accepted server connection's TLS context before the application can query it.

The first verified build produced MsQuic `2.7.0.0-private`. Its dependency table contained only Windows system DLLs, while the matching OpenSSL CLI reported `OpenSSL 3.5.9-dev` and listed `ML-KEM-512`, `ML-KEM-768`, `ML-KEM-1024`, and `X25519MLKEM768`.
