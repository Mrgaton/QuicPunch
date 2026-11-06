# Transporte QUIC automático — 11 de octubre de 2026

La librería prepara QUIC automáticamente para los peers descubiertos con identidad autenticada, dentro del descubrimiento configurado del nodo. La conexión conserva la comprobación del certificado y de la ruta de señalización. No concede confianza ni abre protocolos de aplicaciones.

`PeerTransportOptions`, configurable en el nodo o mediante `QuicPunchBuilder.WithPeerTransport`, gobierna la preparación: 64 peers, cuatro intentos automáticos simultáneos, plazo de 30 segundos por intento, backoff de 5 a 60 segundos y cierre tras cinco minutos sin sesiones. Las preparaciones entrantes también consumen el límite de peers. Las reservas compartidas por identidad mantienen la coordinación de conexiones simultáneas y se liberan en errores, cancelación y Stop. Una generación anterior no puede reservar espacio tras restart.

El planificador sustituye las tareas independientes lanzadas desde Hello. Los peers provisionales del buzón esperan a completar Hello. Un nuevo nonce de sesión permite volver a preparar un peer reiniciado. Los transportes inactivos se aparcan y el mensaje `TransportIdle` comunica el cierre al otro extremo, evitando que vuelva a abrir el túnel automáticamente. Los heartbeats y Hello periódicos no cuentan como actividad de aplicaciones.

Las sesiones virtuales comprueban confianza tanto al iniciar como al recibir una solicitud. Voz y los demás protocolos conservan su autorización; chat mantiene la política de identidades de confianza existente. Retirar confianza cierra las sesiones de aplicaciones sin cerrar inmediatamente el transporte. Streams y datagramas no se entregan a identidades sin confianza.

Se retiraron las opciones y APIs de aceptación global y por peer, el bypass de aplicaciones para identidades sin confianza, sus parámetros de constructor/builder, preferencias, endpoints HTTP, campos de estado y controles de UI. No hay adaptadores de compatibilidad que restauren esa política. La UI presenta estado QUIC y confianza por separado; confiar en un peer ya no dispara por sí mismo una conexión.

Los fixtures que prueban negociaciones explícitas desactivan el planificador automático. Las pruebas de aplicaciones conceden confianza y autorización de sus protocolos concretos. Las pruebas de denegación mantienen peers sin confianza; no se relajan las comprobaciones de producción para hacer pasar las suites.

## Validación

Compilan QuicNet, la librería, el harness de pruebas y Android, con cero errores. Persisten los avisos previos de nulabilidad, APIs UDP obsoletas y disponibilidad de APIs QUIC en Android.

| Suite | Resultado |
| --- | --- |
| `--test-automatic-transport` | OK: QUIC nativo sin confianza, bloqueo local/remoto de aplicaciones, consentimiento de voz, datos sobre el túnel preparado, retirada de confianza, inactividad asimétrica, capacidad entrante, límite concurrente, backoff y restart |
| `--test-security` | OK |
| `--test-singleflight` | OK |
| `--test-handshake-cancellation` | OK |
| `--test-connection-recovery` | OK |
| `--test-multiplexer` | OK |
| `--test-datagram-categorization` | OK: audio, cursor y vídeo fragmentado por QUIC nativo |
| `--test-peer-acl` | OK |
| `--test-webui-security` | OK |
| `--test-connectivity-security` | OK |
| `--test-peer-state` | OK |
| `--test-nostr-trust` | OK |

También pasan el análisis sintáctico de `dashboard.js`, la prueba de estados del dashboard y la comprobación de whitespace del diff. No quedan referencias a las opciones y endpoints retirados en código, pruebas o documentación.

```powershell
dotnet build QuicPunchTests/QuicPunchTests.csproj --artifacts-path .codex-build/peer-transport-final
dotnet .codex-build/peer-transport-final/bin/QuicPunchTests/debug/QuicPunchTests.dll --test-automatic-transport
dotnet build QuicPunch.AndroidProbe/QuicPunch.AndroidProbe.csproj --artifacts-path .codex-build/peer-transport-android
node QuicPunchTests/Tests/PeerConnectionStateWebTests.mjs
```

QuicNet actualizado está en `.codex-build/peer-transport-final/bin/QuicNet/debug/QuicNet.exe`. Los procesos que ya estaban ejecutándose mantienen su versión cargada; hay que reiniciar ambos peers con la nueva compilación para comprobar el caso original entre equipos y NAT distintos. Las pruebas locales no sustituyen esa validación WAN ni una prueba en un dispositivo Android.

Configuración y comportamiento: [Transporte automático de peers](../core/peer-transport.md).
