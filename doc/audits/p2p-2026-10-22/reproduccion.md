# Reproducción de la auditoría P2P del 22 de octubre de 2026

Este anexo conserva el código del diagnóstico como documentación. No incorpora pruebas ni cambios al producto. Para repetirlo, reconstruye los siguientes archivos bajo `.artifacts/p2p-audit-2026-10-22/`, manteniendo exactamente la estructura indicada. Sus `bin/` y `obj/` se generan dentro del proyecto temporal. Las fuentes completas están en los bloques siguientes; se pueden copiar manualmente o extraer con el comando al final.

El ejecutor utiliza reflexión exclusivamente dentro de su proceso para seleccionar pruebas existentes, configurar sus fixtures y controlar los puntos de sincronización. Los streams de las reproducciones son simulados. El benchmark y varias suites existentes sí usan QUIC local real. No ejecutar este proyecto contra perfiles de usuario o datos de producción.

## Harness/Audit.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AllowUnsafeBlocks>true</AllowUnsafeBlocks></PropertyGroup>
  <ItemGroup><ProjectReference Include="../../../QuicPunchTests/QuicPunchTests.csproj" /></ItemGroup>
</Project>
```

## Harness/Program.cs

```csharp
using System.Reflection;
if (args.FirstOrDefault() == "--audit-repros") return await Repros.Run();
if (args.FirstOrDefault() == "--audit-parser-fuzz") return Repros.Fuzz();
var assembly = Assembly.Load("QuicPunchTests");
assembly.GetType("QuicPunchTests.Fixtures.LoopbackOnlyNetwork")!.GetMethod("Configure", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
if (args.FirstOrDefault() == "--audit-security-methods")
{
    string[] names = ["TestAtomicIdentityPersistenceAndPermissions", "TestIPAddressValidationAndFiltering", "TestDeterministicRoleAssignmentAndPreciseTimeResilience", "TestQuicConnectionTransportIntrospectionAndStunBatching", "TestHelloAntiReplayAndSaltDerivation", "TestPeerTrustAndTransportPolicy", "TestNostrDiscoveryTokenAndSchnorrRoundTrip", "TestIpRateLimiterEvictionAndBoundedCardinality", "TestDiscoveredPeerPruningAndBoundedAdmission", "TestHandshakeCandidateFramingIsBounded", "TestIncomingHandshakeSessionsLifecycleAndHardCap", "TestEndpointCacheBinaryProtocolSerializationAndLoading", "TestTorTokenFixedOffsetEncodingAndDecoding", "TestDeterministicNostrIdentityDerivation", "TestTokenCachingAndStability", "TestDummyQuicConnectionTransportLeaveOpen", "TestRandomIdentityNamesAndCertificateSanitization", "TestAckSharePeersTransportIsolation"];
    int failed = 0;
    foreach (string name in names)
    {
        try { var result = assembly.GetType("QuicPunchTests.Tests.SecurityDiscoveryTests")!.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null); if (result is Task t) await t.WaitAsync(TimeSpan.FromSeconds(40)); Console.WriteLine("METHOD PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("METHOD FAIL " + name + ": " + (ex is TargetInvocationException ? ex.InnerException : ex)); }
    }
    return failed == 0 ? 0 : 1;
}
var main = assembly.GetType("QuicPunchTests.Program")!.GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!;
return await (Task<int>)main.Invoke(null, new object[] { args })!;
```

## Harness/Repros.cs

```csharp
using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using QuicNet.Watch.Domain;
using QuicNet.Watch.Protocols;
using QuicNet.Watch.Services;
using QuicPunch;

internal static class Repros
{
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    public static int Fuzz()
    {
        var random = new Random(8489);
        var reassembler = new QuicPunch.Datagrams.DatagramFrameReassembler();
        int parsed = 0, tokens = 0, unexpected = 0;
        for (int i = 0; i < 30000; i++)
        {
            byte[] bytes = new byte[random.Next(i % 3 == 0 ? 19 : 513)]; random.NextBytes(bytes);
            if (i % 2 == 0 && bytes.Length > 0) bytes[0] = QuicPunch.Datagrams.QuicDatagramEnvelope.MagicMarker;
            if (QuicPunch.Datagrams.QuicDatagramEnvelope.TryParse(bytes, out var envelope))
            { parsed++; reassembler.TryProcessFragment(envelope, out _, out _, out _); }
        }
        for (int i = 0; i < 2000; i++)
        {
            byte[] bytes = new byte[random.Next(1, 257)]; random.NextBytes(bytes);
            string token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            try { using var peer = QuicPunch.Helpers.Utilities.DecodeEndpointToken(token); tokens++; }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException or ArgumentException or CryptographicException) { }
            catch (Exception ex) { unexpected++; Console.WriteLine("FUZZ unexpected: " + ex.GetType().Name); }
        }
        Console.WriteLine($"FUZZ seed=8489 datagrams=30000 parsed={parsed}; tokens=2000 decoded={tokens}; unexpectedExceptions={unexpected}");
        return unexpected == 0 ? 0 : 1;
    }
    public static async Task<int> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "qp-p2p-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Console.WriteLine("REPRO_TEMP=" + root);
        try
        {
            await Cache(root);
            await TorIdentity(root);
            await Logger(root);
            await RangePressure(Path.Combine(root, "dispose-first"), true);
            await RangePressure(Path.Combine(root, "cancel-first"), false);
            return 0;
        }
        finally
        {
            string full = Path.GetFullPath(root);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("qp-p2p-audit-"))
                throw new InvalidOperationException("Unsafe temporary cleanup path");
            try { Directory.Delete(full, recursive: true); }
            catch (IOException) { Console.WriteLine("REPRO_CLEANUP_AFTER_PROCESS_EXIT=" + full); }
        }
    }

    private static async Task Logger(string root)
    {
        using var sink = new DelayedSink();
        await using var logger = new QuicPunch.Helpers.RotatingFileLogger(Path.Combine(root, "logger"));
        var writer = new StreamWriter(sink, new System.Text.UTF8Encoding(false), 128, leaveOpen: true);
        typeof(QuicPunch.Helpers.RotatingFileLogger).GetField("_currentWriter", PrivateInstance)!.SetValue(logger, writer);
        typeof(QuicPunch.Helpers.RotatingFileLogger).GetField("_currentDateStr", PrivateInstance)!.SetValue(logger, DateTime.Now.ToString("yyyy-MM-dd"));
        logger.Write(new string('L', 8192));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try { await logger.FlushAsync(); Console.WriteLine("LOGGER FlushAsync unexpectedly succeeded"); }
        catch (InvalidOperationException ex) { Console.WriteLine("LOGGER pending disk write + empty queue: " + ex.GetType().Name + ": " + ex.Message); }
        finally { sink.Release.TrySetResult(); }
        await Task.Delay(50);
        await logger.DisposeAsync();
    }

    private sealed class DelayedSink : Stream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) { }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static async Task Cache(string root)
    {
        string cache = Path.Combine(root, "cache");
        using var control = new WatchControlHandler();
        using var transfer = new MediaTransferHandler();
        using var library = new MediaLibrary(transfer, cache);
        var local = Guid.NewGuid();
        var rooms = new RoomCoordinator(control, library, local);
        using var owner = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        MethodInfo handle = typeof(RoomCoordinator).GetMethod("HandleMessageAsync", PrivateInstance)!;
        Guid roomId = Guid.NewGuid();
        for (int i = 0; i < 24; i++)
        {
            var wire = new WatchWireMessage(i == 0 ? "room_invite" : "media", roomId, owner.Id,
                MediaId: Guid.NewGuid(), Revision: i, MediaName: "fixture.mp4", MediaSize: 4096,
                ChunkSize: 4096, TargetPeerId: local);
            await (Task)handle.Invoke(rooms, [owner, wire])!;
        }
        var files = Directory.GetFiles(cache);
        Console.WriteLine($"CACHE before leave: files={files.Length}; bytes={files.Sum(f => new FileInfo(f).Length)}");
        await rooms.LeaveAsync(default);
        Console.WriteLine($"CACHE after leave: roomIsNull={rooms.Snapshot() == null}; files={Directory.GetFiles(cache).Length}; entries={((IDictionary)typeof(MediaLibrary).GetField("_remoteCaches", PrivateInstance)!.GetValue(library)!).Count}");
        library.Dispose();
        Console.WriteLine($"CACHE after dispose: files={Directory.GetFiles(cache).Length}; bytes={Directory.GetFiles(cache).Sum(f => new FileInfo(f).Length)}");
    }

    private static async Task TorIdentity(string root)
    {
        using var control = new WatchControlHandler();
        using var transfer = new MediaTransferHandler();
        using var library = new MediaLibrary(transfer, Path.Combine(root, "tor-cache"));
        using var localTor = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        using var remoteTor = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        using var remoteForSend = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        remoteForSend.ActiveTransport = QuicPunch.QuicPunch.TransportType.Tor;
        Guid localWanId = Guid.NewGuid();
        Guid remoteWanId = Guid.NewGuid();
        var receiver = new RoomCoordinator(control, library, localWanId);
        control.OnLocalIdentityProviderAttached(_ => remoteTor);
        var input = new WatchWireMessage("room_invite", Guid.NewGuid(), remoteWanId, TargetPeerId: localTor.Id);
        var transformed = (WatchWireMessage)typeof(WatchControlHandler).GetMethod("ForPeer", PrivateInstance)!.Invoke(control, [remoteForSend, input])!;
        await (Task)typeof(RoomCoordinator).GetMethod("HandleMessageAsync", PrivateInstance)!.Invoke(receiver, [remoteTor, transformed])!;
        Console.WriteLine($"TOR invite: authenticatedSenderMatches={transformed.SenderPeerId == remoteTor.Id}; targetMatchesLocalTor={transformed.TargetPeerId == localTor.Id}; accepted={receiver.Snapshot() != null}");
        var wanTarget = transformed with { TargetPeerId = localWanId };
        await (Task)typeof(RoomCoordinator).GetMethod("HandleMessageAsync", PrivateInstance)!.Invoke(receiver, [remoteTor, wanTarget])!;
        Console.WriteLine($"TOR control with WAN target: accepted={receiver.Snapshot() != null}");
    }

    private static async Task RangePressure(string root, bool closeBeforeCancel)
    {
        Directory.CreateDirectory(root);
        Console.WriteLine("RANGE mode: " + (closeBeforeCancel ? "dispose-before-cancel" : "cancel-and-join-before-dispose"));
        using var peer = new PeerInfo().SetCertificateHash(RandomNumberGenerator.GetBytes(32));
        using var control = new WatchControlHandler();
        using var transfer = new MediaTransferHandler();
        using var library = new MediaLibrary(transfer, Path.Combine(root, "pressure-cache"));
        Guid local = Guid.NewGuid();
        var rooms = new RoomCoordinator(control, library, local);
        await rooms.CreateRoomAsync("fixture", [peer.Id], default);
        string source = Path.Combine(root, "source.bin");
        File.WriteAllBytes(source, new byte[65536]);
        var manifest = library.SelectLocalMedia(source, local);
        await rooms.SetMediaAsync(manifest, default);
        var realServe = transfer.RangeRequested!;
        int entered = 0, completed = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobs = new System.Collections.Concurrent.ConcurrentBag<Task>();
        transfer.RangeRequested = async (p, request, ct) =>
        {
            if (Interlocked.Increment(ref entered) == 64) allStarted.TrySetResult();
            var task = realServe(p, request, ct);
            jobs.Add(task);
            try { await task; } finally { Interlocked.Increment(ref completed); }
        };
        using var input = new MemoryStream();
        for (int i = 0; i < 64; i++)
        {
            byte[] frame = new byte[49]; frame[0] = 1;
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(1), 44);
            manifest.MediaId.TryWriteBytes(frame.AsSpan(5, 16));
            Guid.NewGuid().TryWriteBytes(frame.AsSpan(21, 16));
            BinaryPrimitives.WriteInt64LittleEndian(frame.AsSpan(37), 0);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(45), 65536);
            input.Write(frame);
        }
        using var stream = new BlockedDuplex(input.ToArray());
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task session = transfer.HandleAsync(null!, stream, peer, lifetime.Token);
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        Console.WriteLine($"RANGE pressure: requestBytes={input.Length}; callbacksStarted={entered}; completed={completed}; writerBlocked={stream.WriteEntered.Task.IsCompleted}; outstandingTasks={jobs.Count(t => !t.IsCompleted)}");
        if (closeBeforeCancel)
        {
            stream.EndInput.TrySetResult();
            await session.WaitAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine("RANGE session ended before lifetime cancellation");
        }
        lifetime.Cancel();
        try { await Task.WhenAll(jobs.ToArray()).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception e) { Console.WriteLine("RANGE jobs exit: " + e.GetType().Name); }
        if (!closeBeforeCancel) { stream.EndInput.TrySetResult(); await session.WaitAsync(TimeSpan.FromSeconds(3)); }
        await Task.Delay(30);
        bool exclusiveOpen;
        try { using var check = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None); exclusiveOpen = true; } catch (IOException) { exclusiveOpen = false; }
        Console.WriteLine($"RANGE cleanup: callbacksCompleted={completed}; outstandingTasks={jobs.Count(t => !t.IsCompleted)}; sourceReleased={exclusiveOpen}");
    }

    private sealed class BlockedDuplex(byte[] input) : Stream
    {
        private readonly MemoryStream _input = new(input);
        public TaskCompletionSource EndInput { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_input.Position < _input.Length) return await _input.ReadAsync(buffer, ct);
            await EndInput.Task; return 0;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { WriteEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Flush() { }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long n) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
    }
}
```

## run-suites.ps1

```powershell
param([string[]]$Only = @(), [string]$ResultFile = 'suite-results.csv')
$ErrorActionPreference = 'Stop'
$auditRoot = $PSScriptRoot
$harness = Join-Path $auditRoot 'Harness/bin/Test/net11.0/Audit.dll'
$suites = @('sha3','antireplay','punch-packet','peer-acl','session-recovery','resumable-files','critical-reliability','connectivity-security','connection-recovery','peer-state','vpn-policy','vpn-orchestration','vpn-broker','virtual-disk','updates','webui-security','nostr-mailbox','tor-privacy','pqc','quic-datagrams','quic-optimizations','lan-pooling','nat-pin-coordinator','lan-ports','opus')
$results = @()
if ($Only.Count -gt 0) { $suites = $Only }
foreach ($suite in $suites) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.ArgumentList.Add($harness)
    $info.ArgumentList.Add("--test-$suite")
    $info.ArgumentList.Add('--verbose')
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $done = $process.WaitForExit(120000)
    if (-not $done) { $process.Kill($true); $process.WaitForExit() }
    $timer.Stop()
    ($stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()) | Set-Content (Join-Path $auditRoot "$suite.log")
    $entry = [pscustomobject]@{Suite=$suite;Exit=$process.ExitCode;TimedOut=(-not $done);Seconds=[Math]::Round($timer.Elapsed.TotalSeconds,2)}
    $results += $entry
    $entry | ConvertTo-Json -Compress | Write-Output
    $results | Export-Csv (Join-Path $auditRoot $ResultFile) -NoTypeInformation
    $process.Dispose()
}
```

## Comandos

Desde la raíz del repositorio, con el SDK y Node indicados en el informe:

```powershell
dotnet build .artifacts/p2p-audit-2026-10-22/Harness/Audit.csproj -c Test --nologo -v quiet
dotnet .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/Audit.dll --audit-repros
dotnet .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/Audit.dll --audit-parser-fuzz
dotnet .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/Audit.dll --audit-security-methods
& .artifacts/p2p-audit-2026-10-22/run-suites.ps1
& .artifacts/p2p-audit-2026-10-22/run-suites.ps1 -Only automatic-transport,discovery-announcements,discovery-presence,contact-history,geolocation,token-readiness,platform-video,notifications -ResultFile extra-suite-results.csv
dotnet .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/Audit.dll --test-rotating-logger --verbose
dotnet .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/Audit.dll --benchmark-quic --verbose
& scripts/test.ps1 -Web -NoBuild
dotnet list QuicPunchTests/QuicPunchTests.csproj package --vulnerable --include-transitive --format json
```

El runner predeterminado reproduce la prueba parcialmente bloqueada del catálogo STUN y el logger puede pasar o fallar según el orden de ejecución. Para confirmar F05 de forma controlada usa `--audit-repros`. La consulta NuGet puede contactar las fuentes de paquetes configuradas; las pruebas aisladas no la necesitan.

El flag `--audit-repros` no es una suite de regresión que deba dar verde al producto. Imprime las observaciones defectuosas; hay que comparar sus líneas con los resultados esperados del informe. El diagnóstico es reutilizable, pero los valores de IDs y el número exacto de tareas atrapadas varían. Su modo de control debe terminar 64/64 trabajos y liberar el origen.

## Extracción opcional de los bloques

El siguiente script restaura exclusivamente los cuatro archivos temporales descritos arriba. Verifica el destino dentro de `.artifacts` y no altera fuentes del producto:

```powershell
$auditRepo = (Get-Location).Path
$auditDoc = Get-Content -LiteralPath 'doc/audits/p2p-2026-10-22/reproduccion.md' -Raw
$auditTemp = [IO.Path]::GetFullPath((Join-Path $auditRepo '.artifacts/p2p-audit-2026-10-22'))
$auditAllowed = [IO.Path]::GetFullPath((Join-Path $auditRepo '.artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $auditTemp.StartsWith($auditAllowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destino temporal inválido' }
$auditFiles = @('Harness/Audit.csproj','Harness/Program.cs','Harness/Repros.cs','run-suites.ps1')
foreach ($auditFile in $auditFiles) {
    $auditPattern = '(?ms)^## ' + [regex]::Escape($auditFile) + '\r?\n\r?\n```[^\r\n]*\r?\n(.*?)\r?\n```'
    $auditMatch = [regex]::Match($auditDoc, $auditPattern)
    if (-not $auditMatch.Success) { throw "Falta el bloque $auditFile" }
    $auditOutput = [IO.Path]::GetFullPath((Join-Path $auditTemp $auditFile))
    if (-not $auditOutput.StartsWith($auditAllowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Ruta fuera de temporales' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($auditOutput)) | Out-Null
    [IO.File]::WriteAllText($auditOutput, $auditMatch.Groups[1].Value + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}
```

## Limpieza de datos del diagnóstico

Las pruebas de cierre pueden conservar deliberadamente un archivo abierto hasta que termina su proceso. Cada ejecución imprime `REPRO_TEMP=...`. Después de que ese proceso haya salido, se puede eliminar exactamente ese directorio, verificando que está dentro de la carpeta temporal y que su nombre empieza por `qp-p2p-audit-`. No utilizar el patrón como borrado general sobre Temp. Los materiales bajo `.artifacts` son regenerables mediante este anexo y el script estándar de limpieza puede eliminarlos.
