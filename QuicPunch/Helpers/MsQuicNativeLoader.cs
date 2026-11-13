using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace QuicPunch.Helpers;

/// <summary>
/// Loads an application-local MsQuic build before System.Net.Quic initializes.
/// </summary>
public static class MsQuicNativeLoader
{
    private static IntPtr _libraryHandle;
    private static int _initialized;

    public static bool IsCustomLibraryLoaded => _libraryHandle != IntPtr.Zero;

    public static string? LoadedPath { get; private set; }

#pragma warning disable CA2255 // Deliberate: native QUIC must load before any System.Net.Quic static initialization.
    [ModuleInitializer]
    internal static void InitializeModule() => EnsureLoaded();
#pragma warning restore CA2255

    public static void LoadFromPath(string path)
    {
        if (_libraryHandle != IntPtr.Zero) return;
        try
        {
            _libraryHandle = NativeLibrary.Load(path);
            LoadedPath = path;
            _initialized = 1;
            QuicPunchLog.Info($"[MsQuic] Loaded native library from path: {path}");
            if (OperatingSystem.IsWindows()) _ = MsQuicTuner.IsSupported;
        }
        catch (Exception ex)
        {
            QuicPunchLog.Error($"[MsQuic] Failed to load native library from path '{path}': {ex.Message}");
        }
    }

    internal static void EnsureLoaded()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        if (OperatingSystem.IsAndroid())
        {
            if (NativeLibrary.TryLoad("libmsquic.so", typeof(MsQuicNativeLoader).Assembly, null, out var handle) ||
                NativeLibrary.TryLoad("libmsquic.so", out handle))
            {
                _libraryHandle = handle;
                LoadedPath = "libmsquic.so";
                QuicPunchLog.Info("[MsQuic] Loaded Android native library: libmsquic.so");
            }
            return;
        }

        string? libraryName = OperatingSystem.IsWindows()
            ? "msquic.dll"
            : (OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid())
                ? "libmsquic.so"
                : null;

        if (libraryName == null || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException(
                "QuicPunch requires its application-local post-quantum MsQuic build and currently supports only Windows x64 and Linux x64.");
        }

        string? bundledDirectory = NativeAssetBundle.Prepare();
        string localPath = Path.Combine(bundledDirectory ?? AppContext.BaseDirectory, libraryName);
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException(
                "The mandatory application-local post-quantum MsQuic library is missing. " +
                "Build QuicPunch so QuicPunch.NativeBuilder can generate and copy it.",
                localPath);
        }

        try
        {
            _libraryHandle = NativeLibrary.Load(localPath);
            LoadedPath = localPath;
            // Resolve our own ABI from the same verified bundle. No .NET runtime
            // singleton initialization or temporary process-wide base-directory changes.
            NativeLibrary.SetDllImportResolver(typeof(MsQuicNativeLoader).Assembly, (name, assembly, searchPath) =>
                name == "qp_transport" ? NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(localPath)!,
                    OperatingSystem.IsWindows() ? "qp_transport.dll" : "libqp_transport.so")) : IntPtr.Zero);
            QuicPunchLog.Info($"[MsQuic] Loaded application-local native library: {localPath}");
            // Set global XDP policy before the first listener reserves its binding.
            if (OperatingSystem.IsWindows()) _ = MsQuicTuner.IsSupported;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _initialized, 0);
            throw new InvalidOperationException(
                $"Failed to load the application-local MsQuic library at '{localPath}'.",
                ex);
        }
    }
}
