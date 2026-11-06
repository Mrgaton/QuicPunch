# Evidencia conservada de la auditoría P2P del 22 de octubre de 2026

Los extractos siguientes proceden de las ejecuciones locales de la auditoría. No se han sustituido fallos por resultados esperados. Las rutas temporales y los IDs públicos de fixtures no son identidades de publicación. El estado de las fuentes puede seguir cambiando por trabajo concurrente; los hashes delimitan lo observado.

## suite-results.csv

```text
"Suite","Exit","TimedOut","Seconds"
"sha3","0","False","0,13"
"antireplay","0","False","0,1"
"punch-packet","0","False","0,12"
"peer-acl","0","False","0,14"
"session-recovery","0","False","5,91"
"resumable-files","0","False","1,63"
"critical-reliability","0","False","3,36"
"connectivity-security","0","False","1,27"
"connection-recovery","0","False","10,22"
"peer-state","0","False","2,47"
"vpn-policy","0","False","0,17"
"vpn-orchestration","0","False","14,28"
"vpn-broker","0","False","0,15"
"virtual-disk","0","False","0,93"
"updates","0","False","1,36"
"webui-security","0","False","1,94"
"nostr-mailbox","0","False","0,38"
"tor-privacy","0","False","0,49"
"pqc","0","False","0,21"
"quic-datagrams","0","False","0,4"
"quic-optimizations","0","False","0,58"
"lan-pooling","0","False","0,09"
"nat-pin-coordinator","1","False","2,92"
"lan-ports","0","False","2,51"
"opus","0","False","0,24"
```

## extra-suite-results.csv

```text
"Suite","Exit","TimedOut","Seconds"
"automatic-transport","0","False","15,64"
"discovery-announcements","0","False","1,19"
"discovery-presence","0","False","3,74"
"contact-history","0","False","0,19"
"geolocation","0","False","0,45"
"token-readiness","0","False","0,38"
"platform-video","0","False","0,63"
"notifications","0","False","3,94"
```

## logger-results.csv

```text
"Suite","Exit","TimedOut","Seconds"
"rotating-logger","1","False","0,15"
```

## logger-repeats.csv

```text
"Run","Exit","AsyncIoRace"
"1","0","False"
"2","0","False"
"3","0","False"
"4","0","False"
"5","0","False"
"6","0","False"
"7","0","False"
"8","0","False"
```

## nuget-audit.json

```text
{
  "version": 1,
  "parameters": "--vulnerable --include-transitive",
  "sources": [
    "https://api.nuget.org/v3/index.json",
    "https://nuget.telerik.com/v3/index.json"
  ],
  "projects": [
    {
      "path": "C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunchTests/QuicPunchTests.csproj"
    }
  ]
}
```

## parser-fuzz.log

```text
[20:06:48.230] [MsQuic] Loaded application-local native library: C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\.artifacts\p2p-audit-2026-10-22\Harness\bin\Test\net11.0\msquic.dll
FUZZ seed=8489 datagrams=30000 parsed=29065; tokens=2000 decoded=4; unexpectedExceptions=0
```

## repros-final-1.log

```text
REPRO_TEMP=C:\Users\Mrgaton\AppData\Local\Temp\qp-p2p-audit-007bed8727944aea9eb48b188cbccea2
[20:10:21.379] [MsQuic] Loaded application-local native library: C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\.artifacts\p2p-audit-2026-10-22\Harness\bin\Test\net11.0\msquic.dll
CACHE before leave: files=24; bytes=98304
CACHE after leave: roomIsNull=True; files=24; entries=24
CACHE after dispose: files=24; bytes=98304
TOR invite: authenticatedSenderMatches=True; targetMatchesLocalTor=True; accepted=False
TOR control with WAN target: accepted=True
LOGGER pending disk write + empty queue: InvalidOperationException: The stream is currently in use by a previous operation on the stream.
RANGE mode: dispose-before-cancel
RANGE pressure: requestBytes=3136; callbacksStarted=64; completed=0; writerBlocked=True; outstandingTasks=64
RANGE session ended before lifetime cancellation
RANGE jobs exit: TimeoutException
RANGE cleanup: callbacksCompleted=60; outstandingTasks=4; sourceReleased=False
RANGE mode: cancel-and-join-before-dispose
RANGE pressure: requestBytes=3136; callbacksStarted=64; completed=0; writerBlocked=True; outstandingTasks=64
RANGE jobs exit: OperationCanceledException
RANGE cleanup: callbacksCompleted=64; outstandingTasks=0; sourceReleased=True
REPRO_CLEANUP_AFTER_PROCESS_EXIT=C:\Users\Mrgaton\AppData\Local\Temp\qp-p2p-audit-007bed8727944aea9eb48b188cbccea2
```

## repros-final-2.log

```text
REPRO_TEMP=C:\Users\Mrgaton\AppData\Local\Temp\qp-p2p-audit-e592a094126b4ef6954b9a943c28fc5e
[20:10:25.094] [MsQuic] Loaded application-local native library: C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\.artifacts\p2p-audit-2026-10-22\Harness\bin\Test\net11.0\msquic.dll
CACHE before leave: files=24; bytes=98304
CACHE after leave: roomIsNull=True; files=24; entries=24
CACHE after dispose: files=24; bytes=98304
TOR invite: authenticatedSenderMatches=True; targetMatchesLocalTor=True; accepted=False
TOR control with WAN target: accepted=True
LOGGER pending disk write + empty queue: InvalidOperationException: The stream is currently in use by a previous operation on the stream.
RANGE mode: dispose-before-cancel
RANGE pressure: requestBytes=3136; callbacksStarted=64; completed=0; writerBlocked=True; outstandingTasks=64
RANGE session ended before lifetime cancellation
RANGE jobs exit: TimeoutException
RANGE cleanup: callbacksCompleted=49; outstandingTasks=15; sourceReleased=False
RANGE mode: cancel-and-join-before-dispose
RANGE pressure: requestBytes=3136; callbacksStarted=64; completed=0; writerBlocked=True; outstandingTasks=64
RANGE jobs exit: OperationCanceledException
RANGE cleanup: callbacksCompleted=64; outstandingTasks=0; sourceReleased=True
REPRO_CLEANUP_AFTER_PROCESS_EXIT=C:\Users\Mrgaton\AppData\Local\Temp\qp-p2p-audit-e592a094126b4ef6954b9a943c28fc5e
```

## repros-final-3.log

```text
REPRO_TEMP=C:\Users\Mrgaton\AppData\Local\Temp\qp-p2p-audit-f0d3349821324106be7c7f0ab4f56147
[20:10:28.770] [MsQuic] Loaded application-local native library: C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\.artifacts\p2p-audit-2026-10-22\Harness\bin\Test\net11.0\msquic.dll
CACHE before leave: files=24; bytes=98304
CACHE after leave: roomIsNull=True; files=24; entries=24
CACHE after dispose: files=24; bytes=98304
TOR invite: authenticatedSenderMatches=True; targetMatchesLocalTor=True; accepted=False
TOR control with WAN target: accepted=True
LOGGER pending disk write + empty queue: InvalidOperationException: The stream is currently in use by a previous operation on the stream.
RANGE mode: dispose-before-cancel
RANGE pressure: requestBytes=3136; callbacksStarted=64; completed=0; writerBlocked=True; outstandingTasks=64
RANGE session ended before lifetime cancellation
RANGE jobs exit: TimeoutException
RANGE cleanup: callbacksCompleted=61; outstandingTasks=3; sourceReleased=False
RANGE mode: cancel-and-join-before-dispose
RANGE pressure: requestBytes=3136; callbacksStarted=64; completed=0; writerBlocked=True; outstandingTasks=64
RANGE jobs exit: OperationCanceledException
RANGE cleanup: callbacksCompleted=64; outstandingTasks=0; sourceReleased=True
REPRO_CLEANUP_AFTER_PROCESS_EXIT=C:\Users\Mrgaton\AppData\Local\Temp\qp-p2p-audit-f0d3349821324106be7c7f0ab4f56147
```

## Extractos de otras ejecuciones

### build.log

```text
C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\SecurityDiscoveryTests.cs(3868,17): warning CS8602: Dereference of a possibly null reference. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\QuicPunchTests.csproj]

Build succeeded.

C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\SecurityDiscoveryTests.cs(191,21): warning CS8602: Dereference of a possibly null reference. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\QuicPunchTests.csproj]
C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\SecurityDiscoveryTests.cs(277,21): warning CS8602: Dereference of a possibly null reference. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\QuicPunchTests.csproj]
C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\SecurityDiscoveryTests.cs(3341,30): warning CS8602: Dereference of a possibly null reference. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\QuicPunchTests.csproj]
C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\SecurityDiscoveryTests.cs(3520,17): warning CS8602: Dereference of a possibly null reference. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\QuicPunchTests.csproj]
C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\SecurityDiscoveryTests.cs(3707,22): warning CS8602: Dereference of a possibly null reference. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\QuicPunchTests.csproj]
C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\SecurityDiscoveryTests.cs(3868,17): warning CS8602: Dereference of a possibly null reference. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\QuicPunchTests.csproj]
    6 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.10
```

### nat-pin-coordinator.log

```text
PASS: real UDP sockets authenticate through the exact LAN port despite a broad WAN range
PASS: Hello retains the peer's local port
PASS: direct LAN connection resolves promptly without UPnP or STUN responses
[TEST 2] 3-burst history sliding buffer capacity (FIFO retention)... PASSED
[TEST 3] 3-burst aggregated NAT consensus (Cone vs Symmetric NAT)... PASSED
[TEST 4] ResetMapping cleanly wipes 3-burst history and restores clean state... PASSED
[TEST 5] Dynamic AddServerEndpoints preserves rotation cycle without resetting cursor... PASSED
[TEST 6] StunGatherer robust parsing, seed gathering & background streaming... 
System.Exception: GatherInitialEndpointsAsync returned empty collection.
   at QuicPunchTests.Tests.NatPinCoordinatorTests.RunAsync() in C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\NatPinCoordinatorTests.cs:line 319
   at QuicPunchTests.TestOutput.RunAsync(String name, Func`1 test, Boolean verbose, String logDirectory) in C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\TestOutput.cs:line 23
FAIL --test-nat-pin-coordinator (2,8s)
Log: .artifacts\test-output\test-nat-pin-coordinator-20261022-175341-c437da31b787473bbe0455768b4689ab.log

```

### rotating-logger.log

```text
==================================================
   ROTATING FILE LOGGER & ZSTANDARD COMPRESSION    
==================================================
[TEST 1] Logging writes to date-formatted file in logs directory... 
System.InvalidOperationException: The stream is currently in use by a previous operation on the stream.
   at System.IO.StreamWriter.ThrowAsyncIOInProgress()
   at System.IO.StreamWriter.CheckAsyncTaskInProgress()
   at System.IO.StreamWriter.Flush()
   at QuicPunch.Helpers.RotatingFileLogger.FlushAsync() in C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunch\Helpers\RotatingFileLogger.cs:line 152
   at QuicPunchTests.Tests.RotatingLoggerTests.RunAsync() in C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\Tests\RotatingLoggerTests.cs:line 38
   at QuicPunchTests.TestOutput.RunAsync(String name, Func`1 test, Boolean verbose, String logDirectory) in C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunchTests\TestOutput.cs:line 23
FAIL --test-rotating-logger (0,0s)
Log: .artifacts\test-output\test-rotating-logger-20261022-180741-9089d481d89b4b8dbe0dad14cc3cac7b.log

```

### security-methods.log

```text
METHOD PASS TestAtomicIdentityPersistenceAndPermissions
METHOD PASS TestIPAddressValidationAndFiltering
METHOD PASS TestDeterministicRoleAssignmentAndPreciseTimeResilience
METHOD PASS TestQuicConnectionTransportIntrospectionAndStunBatching
METHOD PASS TestHelloAntiReplayAndSaltDerivation
METHOD PASS TestPeerTrustAndTransportPolicy
METHOD PASS TestNostrDiscoveryTokenAndSchnorrRoundTrip
METHOD PASS TestIpRateLimiterEvictionAndBoundedCardinality
METHOD PASS TestDiscoveredPeerPruningAndBoundedAdmission
METHOD PASS TestHandshakeCandidateFramingIsBounded
METHOD PASS TestIncomingHandshakeSessionsLifecycleAndHardCap
METHOD PASS TestEndpointCacheBinaryProtocolSerializationAndLoading
METHOD PASS TestTorTokenFixedOffsetEncodingAndDecoding
METHOD PASS TestDeterministicNostrIdentityDerivation
METHOD PASS TestTokenCachingAndStability
METHOD PASS TestDummyQuicConnectionTransportLeaveOpen
METHOD PASS TestRandomIdentityNamesAndCertificateSanitization
METHOD PASS TestAckSharePeersTransportIsolation
```

### benchmark-quic.log

```text
CASE prepared=True multiplexed=True cubic_control=False loss_every=500 configured_rtt_ms=80; artificial loopback path, not NIC capacity
SPEEDTEST configured_rtt=80 up_Mbps=416,81 down_Mbps=409,15 baseline_ms=91,61 loaded_ms=113,44
PROXY up_packets=385010 down_packets=370813 up_dropped=770 down_dropped=741
OK --benchmark-quic (13,1s)
```

### solution-debug-build.log

```text
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.Sdk.ImportWorkloads.targets(38,5): error NETSDK1147: To build this project, the following workloads must be installed: android [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunch.AndroidProbe\QuicPunch.AndroidProbe.csproj]
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Sdks\Microsoft.NET.Sdk\targets\Microsoft.NET.Sdk.ImportWorkloads.targets(38,5): error NETSDK1147: To install these workloads, run the following command: dotnet workload restore [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunch.AndroidProbe\QuicPunch.AndroidProbe.csproj]
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Microsoft.Common.CurrentVersion.targets(5131,5): error MSB3027: Could not copy "C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet.Geolocation\bin\Debug\net10.0\QuicNet.Geolocation.dll" to "bin\Debug\net11.0\QuicNet.Geolocation.dll". Exceeded retry count of 10. Failed. The file is locked by: "Microsoft Visual Studio Insiders (23400), QuicNet.Geolocation.App (51172)" [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet.Geolocation.App\QuicNet.Geolocation.App.csproj]
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Microsoft.Common.CurrentVersion.targets(5131,5): error MSB3021: Unable to copy file "C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet.Geolocation\bin\Debug\net10.0\QuicNet.Geolocation.dll" to "bin\Debug\net11.0\QuicNet.Geolocation.dll". The process cannot access the file 'C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet.Geolocation.App\bin\Debug\net11.0\QuicNet.Geolocation.dll' because it is being used by another process. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet.Geolocation.App\QuicNet.Geolocation.App.csproj]
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Microsoft.Common.CurrentVersion.targets(5131,5): error MSB3027: Could not copy "C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet.Shared\bin\Debug\net11.0\QuicNet.Shared.dll" to "bin\Debug\net11.0\QuicNet.Shared.dll". Exceeded retry count of 10. Failed. The file is locked by: "QuicNet (32572)" [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet\QuicNet.csproj]
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Microsoft.Common.CurrentVersion.targets(5131,5): error MSB3021: Unable to copy file "C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet.Shared\bin\Debug\net11.0\QuicNet.Shared.dll" to "bin\Debug\net11.0\QuicNet.Shared.dll". The process cannot access the file 'C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet\bin\Debug\net11.0\QuicNet.Shared.dll' because it is being used by another process. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet\QuicNet.csproj]
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Microsoft.Common.CurrentVersion.targets(5131,5): error MSB3027: Could not copy "C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunch.Vpn\bin\Debug\net11.0\QuicPunch.Vpn.dll" to "bin\Debug\net11.0\QuicPunch.Vpn.dll". Exceeded retry count of 10. Failed. The file is locked by: "QuicNet (32572)" [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet\QuicNet.csproj]
C:\Program Files\dotnet\sdk\11.0.100-rc.2.26504.105\Microsoft.Common.CurrentVersion.targets(5131,5): error MSB3021: Unable to copy file "C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicPunch.Vpn\bin\Debug\net11.0\QuicPunch.Vpn.dll" to "bin\Debug\net11.0\QuicPunch.Vpn.dll". The process cannot access the file 'C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet\bin\Debug\net11.0\QuicPunch.Vpn.dll' because it is being used by another process. [C:\Users\Mrgaton\Syncthing\Programs\CSharp Test Programs\UdpPunchHoleTest\QuicNet\QuicNet.csproj]
```

## Resultados web

El runner enumeró 19 archivos: 15 satisfactorios y cuatro fallidos. Los diagnósticos de los cuatro fallos se conservan a continuación; el runner escribe parte de sus errores directamente en stderr.

### ChatConnectionWebTests

```text
evalmachine.<anonymous>:626
    if (!saved.id || saved.isTrusted === false || byId.has(saved.id) || !QP.peerInConnectionMode(saved, status)) continue;
                                                                            ^

TypeError: QP.peerInConnectionMode is not a function
    at chatTrustedPeers (evalmachine.<anonymous>:626:77)
    at Object.connectChat (evalmachine.<anonymous>:1886:16)
    at file:///C:/Users/Mrgaton/Syncthing/Programs/CSharp%20Test%20Programs/UdpPunchHoleTest/QuicPunchTests/Tests/ChatConnectionWebTests.mjs:64:9

Node.js v24.14.1
```

### ChatNavigationWebTests

```text
evalmachine.<anonymous>:5
  closeEmojiPopover();
  ^

ReferenceError: closeEmojiPopover is not defined
    at Object.selectChatPeer (evalmachine.<anonymous>:5:3)
    at file:///C:/Users/Mrgaton/Syncthing/Programs/CSharp%20Test%20Programs/UdpPunchHoleTest/QuicPunchTests/Tests/ChatNavigationWebTests.mjs:12:9
    at ModuleJob.run (node:internal/modules/esm/module_job:430:25)
    at async onImport.tracePromise.__proto__ (node:internal/modules/esm/loader:661:26)
    at async asyncRunEntryPointWithESMLoader (node:internal/modules/run_main:101:5)

Node.js v24.14.1
```

### PeerConnectionStateWebTests

```text
evalmachine.<anonymous>:189
  const inMode = peer => QP.peerInConnectionMode(peer, s);
                            ^

TypeError: QP.peerInConnectionMode is not a function
    at inMode (evalmachine.<anonymous>:189:29)
    at Array.filter (<anonymous>)
    at renderDashboard (evalmachine.<anonymous>:190:33)
    at evalmachine.<anonymous>:1:1
    at Script.runInContext (node:vm:149:12)
    at file:///C:/Users/Mrgaton/Syncthing/Programs/CSharp%20Test%20Programs/UdpPunchHoleTest/QuicPunchTests/Tests/PeerConnectionStateWebTests.mjs:25:39
    at ModuleJob.run (node:internal/modules/esm/module_job:430:25)
    at async onImport.tracePromise.__proto__ (node:internal/modules/esm/loader:661:26)
    at async asyncRunEntryPointWithESMLoader (node:internal/modules/run_main:101:5)

Node.js v24.14.1
```

### ProfileAvatarWebTests

```text
node:internal/modules/run_main:107
    triggerUncaughtException(
    ^

AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:

524288 !== 327680

    at file:///C:/Users/Mrgaton/Syncthing/Programs/CSharp%20Test%20Programs/UdpPunchHoleTest/QuicPunchTests/Tests/ProfileAvatarWebTests.mjs:9:8
    at ModuleJob.run (node:internal/modules/esm/module_job:430:25)
    at async onImport.tracePromise.__proto__ (node:internal/modules/esm/loader:661:26)
    at async asyncRunEntryPointWithESMLoader (node:internal/modules/run_main:101:5) {
  generatedMessage: true,
  code: 'ERR_ASSERTION',
  actual: 524288,
  expected: 327680,
  operator: 'strictEqual',
  diff: 'simple'
}

Node.js v24.14.1
```

## Archivos cambiados durante la auditoría

| Archivo | SHA256 inicial | SHA256 al conservar evidencia |
| --- | --- | --- |
| QuicPunch/QuicPunch.cs | 4A690ED1C827A1E6C54CE81BAB109216335F53BF455146EC046ECEFAA1B702AC | 9041BA7B81DF3341F20B6C28BC487E5190048E2ECA492487C3D421B23134E0EF |
| QuicNet.Shared/Notifications/AppNotificationService.cs | 3C031FBC58F79DE5710063190814033A70CA6979F078232B79ED6DEF67FBA91C | 843CE255CAABAFA779B6D025EB54A26D2D4134DFD1BE55387960FFECB529B4BC |
| QuicNet.Shared/Serialization/JsonContracts.AppSettings.Context.cs | 0F4BCE6BF218DDD753BAD02CC0D194B6DE906D32FFC8FFCB65203F12CAA4B30F | 24D0182ED32A0BBCD79066EA56FCAED67699EDE03168667B9BFA7879FFFF7686 |
| QuicNet.Shared/Settings/AppPreferencesStore.cs | 170F2271CBFFA25396A29D43A7D8CFB6FFD02CF2B0A0ACC53F275690C1EB8D30 | 6E9641CAD742C292A6BBECD519538B6AADBF8FA85B47F8B75C5DE1622C18FCEA |
| QuicNet.Shared/WebUi/Modules/AppSettingsApiModule.cs | 07026B25927EFDC4E5C28106FEA30800161BD651787D4C633E6B22256D6C5E7D | 5499B182EACFE73F869051DE9B3DC33830DB3AC711F555D40958693FAB515D99 |
| QuicNet.Shared/WebUi/Modules/PeerApiModule.cs | D3E121E11649636616DF871090187FAF51A541233C3272418E5E756692F6A696 | F1A8252819486000672CF8CB39A634198EB1E58ACF4AD64167C4D5C47839ADC5 |
| QuicPunchTests/Tests/AppBehaviorBrowserTests.mjs | 6C3D9CFE047F3F31C41A6E993F2514A1F264B6536EC365A3139F25CBF960F4D9 | 2EE3E25CB3FA4CEB25F92A4BC7A90BC20DF4EFF2E634B1859B42D69D3C7E3433 |
| QuicPunchTests/Tests/NotificationTests.cs | 1187C74D10BB00D095D24A243364D8D851B01A46A43DEBA23EF53C2DF59D8A0C | 8788B31EE5FC7A85784D5F8CE421A83AAF8B45C7F06955FD0DA1294474CB1807 |

Estos cambios no fueron escritos por los parches de la auditoría; no se revirtieron. Las pruebas generales se distribuyen entre compilaciones sucesivas, por lo que no son una ejecución contra un árbol inmutable.

## Hashes de ubicaciones de los hallazgos y ejecutor

| Archivo | SHA256 | Igual al inicial |
| --- | --- | --- |
| QuicNet.Shared/Watch/Services/MediaLibrary.cs | B67374AB1DBF130566F40F3CFCC0755087693371F0D77B3B8C62C8D509435444 | True |
| QuicNet.Shared/Watch/Services/RoomCoordinator.cs | 644C1DC30E23856131D5938AC3A15849D795AC725EDC036D19016AF6F1E090EF | True |
| QuicNet.Shared/Watch/Protocols/MediaTransferHandler.cs | 9DB8C6A15300AC37DA769BE99E65D8085F13D913FDDA0E2DC910466A32E55BBD | True |
| QuicNet.Shared/Watch/Protocols/WatchControlHandler.cs | F90F0F3BA82C29DA286C805A0AB2A018CED3B0116CF8CE59A32019B4D428E3FE | True |
| QuicNet/Program.cs | 7CEEF92E7720DBFF8A987A995B13AAA4557C6F3D5A108C2F42B178E77F936D79 | True |
| QuicPunch/Helpers/RotatingFileLogger.cs | B1F8C775B6C223AFFE2370AA704388C2371E85DC2C78D79E423A126E8EBEBFBE | True |
| QuicPunchTests/Tests/ChatConnectionWebTests.mjs | BCF9E120F447E6630A5155A415FF35D15CE764A5A865D62D74B1C6C5C4FB1AB1 | True |
| QuicPunchTests/Tests/ChatNavigationWebTests.mjs | E2F5463F3E328647D87C6E8769F4CA6AAA4D0FFE6305FE6578805F8127A8102B | True |
| QuicPunchTests/Tests/PeerConnectionStateWebTests.mjs | 536E3A092F26DB359F96BD71024C545714CCB06255A1F99054DE8179E9AEFDF4 | True |
| QuicPunchTests/Tests/ProfileAvatarWebTests.mjs | 8446C3D55A2318502996F3336D691CF950C5D1971FD6889B18308949713353B1 | True |
| QuicNet/wwwroot/app.js | 35B2F75B288409A6558BBC329CE89E5F650A2A5DD3F26D78A6D5121514C1D735 | Fuera de la selección inicial |
| QuicNet/wwwroot/chat.js | 9657B36C640E3B5383BA93CF49E9BECC797E884DD43CD826DE2A14B5491ABFA0 | Fuera de la selección inicial |
| QuicNet/wwwroot/dashboard.js | 31EAAE5AFAC02A270EAD14EE26DC09171C4730B172F8CAAA62FC8FC74C7AF036 | Fuera de la selección inicial |
| QuicNet/wwwroot/avatar-media.js | EFF4E1BFEC52C1768F1B3FC33C4F9DB25C3EACF6BB5805C842C80CD4D85E3652 | Fuera de la selección inicial |
| QuicNet.Shared/Settings/ProfileAvatarStore.cs | 0FA6578A54927918DB6511A9C62DF9FBD4D9753AB2EE3B09687014D6515A9F62 | True |
| QuicNet.Shared/WebUi/WebUiWebSocketHub.cs | A087DB78D5E2FF324F2E14AA4616F6EE066B86CF6174C84121369F31A6EB7212 | True |
| .artifacts/p2p-audit-2026-10-22/Harness/Audit.csproj | 57D92DCB9C483907A9D2B312A407411A6111D5132A8CC4E93601052E1D759DA3 | Ejecutor/compilación temporal |
| .artifacts/p2p-audit-2026-10-22/Harness/Program.cs | 8460179138DCF6158B843A8344FB6914B303C51E60E57D6BF51A402A06ACF45F | Ejecutor/compilación temporal |
| .artifacts/p2p-audit-2026-10-22/Harness/Repros.cs | FF31E524A19C25473ABCF5229DACB94C04D86CA05C2273C64D543A57014586D4 | Ejecutor/compilación temporal |
| .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/QuicPunch.dll | B94DA1A3E746E70B13BE868DCF1C8F6A2CBA89D4914CC4746448D94DA1D48470 | Ejecutor/compilación temporal |
| .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/QuicNet.Shared.dll | BF57619F530CD3C7358CD92A8F06ED10D5E5E4B9296E3C89C60748183DB23048 | Ejecutor/compilación temporal |
| .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/QuicPunchTests.dll | D057D7C2CCC90980D4B4F268F7EE342191CB5D7491FA3431538D744F397EACFB | Ejecutor/compilación temporal |
| .artifacts/p2p-audit-2026-10-22/Harness/bin/Test/net11.0/msquic.dll | E997A3EAAF82B2176B441796E5FAA0D61917395AAF8DF73C8930F90315A5D5BA | Ejecutor/compilación temporal |
