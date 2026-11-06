using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

return await NativeBuilder.RunAsync(args);

internal static partial class NativeBuilder
{
    private const string WindowsRid = "win-x64";
    private const string LinuxRid = "linux-x64";

    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            string rid = ParseRid(args);
            string repositoryRoot = FindRepositoryRoot();
            BuildLock buildLock = await ReadLockAsync();
            // OpenSSL's Windows Perl generator still creates paths that can exceed MAX_PATH.
            // Keep the source/build cache name intentionally short on Windows.
            string artifactsRoot = OperatingSystem.IsWindows()
                ? @"C:\qp_nb"
                : Environment.GetEnvironmentVariable("QUICNET_NATIVE_BUILD_CACHE") ?? Path.Combine(repositoryRoot, ".artifacts", "native-builder");
            string sourceDirectory = Path.Combine(artifactsRoot, "msquic");

            ValidateHost(rid);
            Toolchain tools = DiscoverToolchain(rid);
            Directory.CreateDirectory(artifactsRoot);

            await CheckoutSourcesAsync(tools, buildLock, sourceDirectory);
            RemoveRelocatedBuildCache(sourceDirectory);
            ApplyPqcPatch(tools, sourceDirectory, repositoryRoot);
            ApplyCallbackQueryPatch(tools, sourceDirectory, repositoryRoot);
            ApplyAckFrequencyPatch(tools, sourceDirectory, repositoryRoot);
            ApplyMsvcFastFailPatch(tools, sourceDirectory, repositoryRoot);
            VerifyPinnedOpenSsl(sourceDirectory, buildLock.MinimumOpenSslVersion);
            BuildMsQuic(tools, sourceDirectory, rid);
            BuildOpenSslCli(tools, sourceDirectory, rid);

            string builtLibrary = FindBuiltLibrary(sourceDirectory, rid);
            VerifyNativeDependencies(tools, builtLibrary, rid);
            VerifyOpenSslCapabilities(tools, sourceDirectory, buildLock.TlsGroups);
            string destination = CopyArtifact(repositoryRoot, builtLibrary, rid);

            await using FileStream libraryStream = File.OpenRead(destination);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(libraryStream));
            Console.WriteLine($"Built and verified {destination}");
            Console.WriteLine($"SHA256: {hash}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string ParseRid(string[] args)
    {
        if (args.Length == 0)
            return OperatingSystem.IsWindows() ? WindowsRid : LinuxRid;

        if (args.Length == 2 && args[0] == "--rid" && args[1] is WindowsRid or LinuxRid)
            return args[1];

        throw new ArgumentException("Usage: dotnet run --project QuicPunch.NativeBuilder -- [--rid win-x64|linux-x64]");
    }

    private static void ValidateHost(string rid)
    {
        bool matchesHost = rid == WindowsRid && OperatingSystem.IsWindows() ||
                           rid == LinuxRid && OperatingSystem.IsLinux();
        if (!matchesHost)
            throw new PlatformNotSupportedException(
                $"RID '{rid}' must be built on its matching operating system. Use the matrix CI build to produce both artifacts.");

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("The native builder currently supports x64 hosts only.");
    }

    private static async Task<BuildLock> ReadLockAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "native-build.lock.json");
        await using FileStream stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BuildLock>(stream, JsonOptions) ??
               throw new InvalidDataException($"Could not read {path}.");
    }

    private static Toolchain DiscoverToolchain(string rid)
    {
        var additionalPaths = new List<string>();
        if (rid == WindowsRid)
        {
            AddIfDirectory(additionalPaths, @"C:\Program Files\CMake\bin");
            string vsPath = "";
            try { vsPath = FindVisualStudio(); } catch { }
            if (!string.IsNullOrEmpty(vsPath))
            {
                AddIfDirectory(additionalPaths, Path.Combine(vsPath, @"Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin"));
            }
            AddIfDirectory(additionalPaths, @"C:\Strawberry\perl\bin");
            AddIfDirectory(additionalPaths, @"C:\Strawberry\c\bin");
            AddIfDirectory(additionalPaths, @"C:\xampp\perl\bin");
            AddIfDirectory(additionalPaths, @"C:\Program Files\NASM");
            AddIfDirectory(additionalPaths, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bin", "NASM"));
        }

        string path = string.Join(Path.PathSeparator, additionalPaths.Append(Environment.GetEnvironmentVariable("PATH") ?? ""));
        string shell = FindTool("pwsh", path);
        string git = FindTool("git", path);
        string cmake = FindTool("cmake", path);
        _ = FindTool("perl", path);
        _ = FindTool("nasm", path);

        if (rid == WindowsRid)
            _ = FindVisualStudio();
        else
        {
            _ = FindAnyTool(["clang", "gcc"], path);
            _ = FindTool("make", path);
        }

        return new Toolchain(shell, git, cmake, path);
    }

    private static async Task CheckoutSourcesAsync(Toolchain tools, BuildLock buildLock, string sourceDirectory)
    {
        if (!Directory.Exists(Path.Combine(sourceDirectory, ".git")))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceDirectory)!);
            Run(tools.Git, ["clone", "--filter=blob:none", "--no-checkout", buildLock.MsQuicRepository, sourceDirectory], null, tools.Path);
        }

        Run(tools.Git, ["-C", sourceDirectory, "fetch", "--depth", "1", "origin", buildLock.MsQuicCommit], null, tools.Path);
        Run(tools.Git, ["-C", sourceDirectory, "checkout", "--detach", buildLock.MsQuicCommit], null, tools.Path);
        // Tests/tools/perf are disabled below. OpenSSL's nested submodules are
        // fuzz corpora and interop test suites, not inputs to the static library.
        Run(tools.Git, ["-C", sourceDirectory, "submodule", "update", "--init", "--depth", "1", "--",
            "submodules/clog", "submodules/openssl"], null, tools.Path);
        await Task.CompletedTask;
    }

    private static void ApplyPqcPatch(Toolchain tools, string sourceDirectory, string repositoryRoot)
    {
        string tlsSource = Path.Combine(sourceDirectory, "src", "platform", "tls_openssl.c");
        string source = File.ReadAllText(tlsSource);
        if (source.Contains("X25519MLKEM768:X25519", StringComparison.Ordinal) &&
            source.Contains("SSL_get0_group_name", StringComparison.Ordinal))
            return;

        string patch = Path.Combine(repositoryRoot, "QuicPunch.NativeBuilder", "msquic-openssl-pqc.patch");
        Run(tools.Git, ["-C", sourceDirectory, "apply", "--check", patch], null, tools.Path);
        Run(tools.Git, ["-C", sourceDirectory, "apply", patch], null, tools.Path);
    }

    private static void ApplyCallbackQueryPatch(Toolchain tools, string sourceDirectory, string repositoryRoot)
    {
        string source = File.ReadAllText(Path.Combine(sourceDirectory, "src", "core", "connection.c"));
        if (source.Contains("QUICNET_PARAM_CONN_CALLBACK_HANDLER", StringComparison.Ordinal)) return;
        string patch = Path.Combine(repositoryRoot, "QuicPunch.NativeBuilder", "msquic-callback-query.patch");
        Run(tools.Git, ["-C", sourceDirectory, "apply", "--check", patch], null, tools.Path);
        Run(tools.Git, ["-C", sourceDirectory, "apply", patch], null, tools.Path);
    }

    private static void ApplyAckFrequencyPatch(Toolchain tools, string sourceDirectory, string repositoryRoot)
    {
        string source = File.ReadAllText(Path.Combine(sourceDirectory, "src", "core", "send.c"));
        if (source.Contains("ACK_FREQUENCY must respect the receiver", StringComparison.Ordinal)) return;
        string patch = Path.Combine(repositoryRoot, "QuicPunch.NativeBuilder", "msquic-ack-frequency.patch");
        Run(tools.Git, ["-C", sourceDirectory, "apply", "--check", patch], null, tools.Path);
        Run(tools.Git, ["-C", sourceDirectory, "apply", patch], null, tools.Path);
    }

    private static void ApplyMsvcFastFailPatch(Toolchain tools, string sourceDirectory, string repositoryRoot)
    {
        string source = File.ReadAllText(Path.Combine(sourceDirectory, "src", "inc", "quic_platform_winuser.h"));
        if (source.Contains("MSVC 19.52 LTCG", StringComparison.Ordinal)) return;
        string patch = Path.Combine(repositoryRoot, "QuicPunch.NativeBuilder", "msquic-msvc-fastfail.patch");
        Run(tools.Git, ["-C", sourceDirectory, "apply", "--check", patch], null, tools.Path);
        Run(tools.Git, ["-C", sourceDirectory, "apply", patch], null, tools.Path);
    }

    private static void RemoveRelocatedBuildCache(string sourceDirectory)
    {
        string buildDirectory = Path.Combine(sourceDirectory, "build");
        string? cache = Directory.Exists(buildDirectory)
            ? Directory.EnumerateFiles(buildDirectory, "CMakeCache.txt", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (cache == null)
            return;

        string normalizedSource = sourceDirectory.Replace('\\', '/');
        string contents = File.ReadAllText(cache).Replace('\\', '/');
        if (contents.Contains(normalizedSource, StringComparison.OrdinalIgnoreCase))
            return;

        Console.WriteLine($"Removing relocated CMake cache: {buildDirectory}");
        Directory.Delete(buildDirectory, recursive: true);
    }

    private static void VerifyPinnedOpenSsl(string sourceDirectory, string minimumVersion)
    {
        string versionFile = Path.Combine(sourceDirectory, "submodules", "openssl", "VERSION.dat");
        string text = File.ReadAllText(versionFile);
        int major = ReadVersionPart(text, "MAJOR");
        int minor = ReadVersionPart(text, "MINOR");
        Version actual = new(major, minor);
        if (actual < Version.Parse(minimumVersion))
            throw new InvalidDataException($"MsQuic pins OpenSSL {actual}, but {minimumVersion} or newer is required.");

        Console.WriteLine($"MsQuic OpenSSL submodule version: {actual}");
    }

    private static void BuildMsQuic(Toolchain tools, string sourceDirectory, string rid)
    {
        var arguments = new List<string>();
        if (rid == WindowsRid)
        {
            string developmentShell = Path.Combine(FindVisualStudio(), "Common7", "Tools", "Launch-VsDevShell.ps1");
            string generator = DetectVisualStudioGenerator();
            string command = $"& '{developmentShell}' -Arch amd64 -HostArch amd64; " +
                             $"& '{Path.Combine(sourceDirectory, "scripts", "build.ps1")}' " +
                             $"-Config Release -Arch x64 -Platform windows -Tls openssl " +
                             $"-DisableTest -DisableTools -DisablePerf -Generator '{generator}' -Parallel 0; " +
                             "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }";
            arguments.AddRange(["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command]);
        }
        else
        {
            arguments.Add(Path.Combine(sourceDirectory, "scripts", "build.ps1"));
            arguments.AddRange(["-Config", "Release", "-Arch", "x64", "-Platform", "linux", "-Tls", "openssl",
                "-DisableTest", "-DisableTools", "-DisablePerf", "-Parallel", "0"]);
        }

        Run(tools.Shell, arguments, sourceDirectory, tools.Path);
    }

    private static string FindBuiltLibrary(string sourceDirectory, string rid)
    {
        string fileName = rid == WindowsRid ? "msquic.dll" : "libmsquic.so";
        string platform = rid == WindowsRid ? "windows" : "linux";
        string expected = Path.Combine(sourceDirectory, "artifacts", "bin", platform, "x64_Release_openssl", fileName);
        if (!File.Exists(expected))
            throw new FileNotFoundException("MsQuic did not produce the expected native library.", expected);
        return expected;
    }

    private static void BuildOpenSslCli(Toolchain tools, string sourceDirectory, string rid)
    {
        string platform = rid == WindowsRid ? "windows" : "linux";
        string buildDirectory = Path.Combine(sourceDirectory, "build", platform, "x64_openssl", "_deps", "opensslquic-build", "submodules", "openssl");
        if (rid == WindowsRid) buildDirectory = Path.Combine(buildDirectory, "release");
        if (!Directory.Exists(buildDirectory))
            throw new DirectoryNotFoundException($"The expected OpenSSL build directory was not produced: {buildDirectory}");

        if (rid == WindowsRid)
        {
            string developmentShell = Path.Combine(FindVisualStudio(), "Common7", "Tools", "Launch-VsDevShell.ps1");
            string command = $"& '{developmentShell}' -Arch amd64 -HostArch amd64; " +
                             $"Set-Location '{buildDirectory}'; nmake /NOLOGO apps\\openssl.exe; " +
                             "if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }";
            Run(tools.Shell, ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command], sourceDirectory, tools.Path);
        }
        else
        {
            string make = FindTool("make", tools.Path);
            Run(make, ["-C", buildDirectory, "apps/openssl"], sourceDirectory, tools.Path);
        }
    }

    private static void VerifyNativeDependencies(Toolchain tools, string library, string rid)
    {
        if (rid == LinuxRid)
        {
            string ldd = FindTool("ldd", tools.Path);
            string dependencies = Run(ldd, [library], null, tools.Path, captureOutput: true);
            if (DependencyRegex().IsMatch(dependencies))
                throw new InvalidDataException("MsQuic unexpectedly has a dynamic dependency on libssl or libcrypto.");
            return;
        }

        byte[] image = File.ReadAllBytes(library);
        string printable = System.Text.Encoding.ASCII.GetString(image);
        if (DependencyRegex().IsMatch(printable))
            throw new InvalidDataException("MsQuic unexpectedly references a dynamic OpenSSL library.");
    }

    private static void VerifyOpenSslCapabilities(Toolchain tools, string sourceDirectory, string expectedGroups)
    {
        string executableName = OperatingSystem.IsWindows() ? "openssl.exe" : "openssl";
        string buildRoot = Path.Combine(sourceDirectory, "build");
        string? executable = OperatingSystem.IsLinux() ? Path.Combine(sourceDirectory, "build", "linux", "x64_openssl", "_deps", "opensslquic-build", "submodules", "openssl", "apps", "openssl") :
            Directory.EnumerateFiles(buildRoot, executableName, SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}apps{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        if (executable == null)
            throw new FileNotFoundException("The OpenSSL command-line executable was not produced, so PQC capabilities cannot be verified.");

        string version = Run(executable, ["version", "-a"], null, tools.Path, captureOutput: true);
        string kems = Run(executable, ["list", "-kem-algorithms"], null, tools.Path, captureOutput: true);
        string groups = Run(executable, ["list", "-tls1_3", "-tls-groups"], null, tools.Path, captureOutput: true);
        if (!kems.Contains("ML-KEM-768", StringComparison.OrdinalIgnoreCase) ||
            !groups.Contains(expectedGroups.Split(':')[0], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The built OpenSSL does not expose ML-KEM-768 and X25519MLKEM768.");

        Console.WriteLine(version.Trim());
        Console.WriteLine($"Verified ML-KEM-768 and TLS groups {expectedGroups}.");
    }

    private static string CopyArtifact(string repositoryRoot, string library, string rid)
    {
        string destinationDirectory = Path.Combine(repositoryRoot, "QuicPunch", "runtimes", rid, "native");
        Directory.CreateDirectory(destinationDirectory);
        string destination = Path.Combine(destinationDirectory, Path.GetFileName(library));
        File.Copy(library, destination, overwrite: true);
        // The verified output also records that changes to the builder were handled;
        // a no-op native link may otherwise retain an older timestamp indefinitely.
        File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
        return destination;
    }

    private static string Run(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory,
        string path,
        bool captureOutput = false)
    {
        using var process = new Process();
        process.StartInfo.FileName = executable;
        process.StartInfo.WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = captureOutput;
        process.StartInfo.RedirectStandardError = captureOutput;
        process.StartInfo.Environment["PATH"] = path;
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        Console.WriteLine($"> {Path.GetFileName(executable)} {string.Join(' ', process.StartInfo.ArgumentList)}");
        process.Start();
        string stdout = captureOutput ? process.StandardOutput.ReadToEnd() : "";
        string stderr = captureOutput ? process.StandardError.ReadToEnd() : "";
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed with exit code {process.ExitCode}: {executable}{Environment.NewLine}{stdout}{stderr}");
        return stdout + stderr;
    }

    private static string FindTool(string name, string path)
    {
        string[] extensions = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat", ""] : [""];
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (string extension in extensions)
        {
            string candidate = Path.Combine(directory.Trim('"'), name + extension);
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Required native build tool '{name}' was not found. See doc/core/post-quantum-msquic.md for the host toolchain requirement.");
    }

    private static string FindAnyTool(IEnumerable<string> names, string path)
    {
        foreach (string name in names)
        {
            try { return FindTool(name, path); }
            catch (FileNotFoundException) { }
        }
        throw new FileNotFoundException($"None of the required tools were found: {string.Join(", ", names)}.");
    }

    private static string FindVisualStudio()
    {
        string vsWhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vsWhere))
            throw new FileNotFoundException("Visual Studio C++ Build Tools and the Windows SDK are required.", vsWhere);

        string installation = Run(vsWhere,
            ["-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            null, Environment.GetEnvironmentVariable("PATH") ?? "", captureOutput: true).Trim();
        return string.IsNullOrWhiteSpace(installation)
            ? throw new InvalidOperationException("Visual Studio C++ Build Tools were not found.")
            : installation;
    }

    private static string DetectVisualStudioGenerator()
    {
        string version = Run(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe"),
            ["-latest", "-prerelease", "-products", "*", "-property", "installationVersion"], null,
            Environment.GetEnvironmentVariable("PATH") ?? "", captureOutput: true).Trim();
        return Version.Parse(version).Major switch
        {
            17 => "Visual Studio 17 2022",
            18 => "Visual Studio 18 2026",
            int major => throw new PlatformNotSupportedException($"Unsupported Visual Studio major version: {major}.")
        };
    }

    private static int ReadVersionPart(string contents, string name)
    {
        Match match = Regex.Match(contents, $@"(?m)^{name}=(\d+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : throw new InvalidDataException($"Missing {name} in OpenSSL VERSION.dat.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuicPunch.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "QuicPunch")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the QuicPunch repository root.");
    }

    private static void AddIfDirectory(ICollection<string> paths, string path)
    {
        if (Directory.Exists(path))
            paths.Add(path);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [GeneratedRegex(@"(?i)(libssl|libcrypto)(?:[-_.][^\x00\s]*)?\.(?:dll|so)")]
    private static partial Regex DependencyRegex();

    private sealed record Toolchain(string Shell, string Git, string CMake, string Path);

    private sealed record BuildLock(
        string MsQuicRepository,
        string MsQuicCommit,
        string MinimumOpenSslVersion,
        string TlsGroups);
}
