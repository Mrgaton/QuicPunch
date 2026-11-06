# Revisión de la conexión QUIC: Nostr, Hello y arranque simultáneo

## Causa de la espera en los dos registros

En GUARRIDONGO aparece un mensaje global Nostr de SFOT1NQIKH antes de su Hello UDP. `ResolveOrCreatePeerByNostrPubKey` crea una entrada provisional a partir del peer guardado: tiene el hash del certificado y su identificador, pero no el certificado.

`HelloHandler` valida el Hello y deriva una sesión nueva, pero la admisión anterior solo permitía añadir identificadores ausentes. Al encontrar el mismo identificador provisional, descartaba el peer autenticado. Los handshakes posteriores encontraban la entrada provisional y `PeerInfo.Curve` lanzaba `Certificate does not contain an ECDSA public key`. Este mensaje también se produce por un certificado **ausente**; no demuestra que el certificado remoto sea RSA o inválido.

El otro extremo agotaba la negociación y después el hole punch: GUARRIDONGO no había podido verificar el Accept ni empezar las pruebas UDP correspondientes. Al caducar la entrada provisional tras unos 60 segundos, un Hello nuevo podía registrarse. El registro de GUARRIDONGO confirma finalmente una conexión QUIC correcta a las 19:42:49.

## Correcciones

- Un Hello ya verificado, con sesión criptográfica derivada, puede sustituir atómicamente una entrada provisional del mismo identificador. Se conserva su identidad Nostr registrada, se actualizan los índices y se libera la entrada anterior. Los certificados existentes no se sustituyen por anuncios Nostr.
- La resolución de mensajes del buzón devuelve el peer autenticado existente por hash cuando está asociado al remitente guardado. La creación de entradas y su promoción comparten el bloqueo de admisión.
- Handshake, ACK y QUIC_READY verifican firmas mediante una operación que rechaza certificados ausentes o contextos eliminados sin lanzar excepciones al receptor UDP.
- Una conexión solicitada desde un token/entrada provisional completa primero Hello y utiliza el objeto autenticado resultante. La espera tiene un límite de 15 segundos, respeta cancelación y ciclo de vida, y queda dentro de la deduplicación por peer.
- Una entrada provisional reciente ya no se considera una ruta UDP autenticada sana al evaluar la reconexión Nostr.
- La reserva de negociación comienza **antes** de reunir candidatos STUN o preparar el mapeo del router. Se coordina con la admisión entrante para arbitrar conexiones simultáneas incluso durante la preparación. Si ya existe un trabajador entrante, el solicitante espera su multiplexer. La prueba aislada reprodujo una negociación duplicada que antes terminaba por cancelación aunque el trabajador entrante conectara correctamente.
- Las retransmisiones solo reenvían respuestas ya disponibles; no crean tareas esperando una respuesta por cada duplicado durante la preparación.
- Un error en un callback de disponibilidad no elimina el certificado de un peer ya admitido. Los handlers de protocolos reciben el peer del multiplexer, también cuando el solicitante conserva una referencia provisional antigua.
- El error de creación Wintun incluye ahora el código y la descripción Win32 originales. Los registros anteriores no contienen ese dato, por lo que no permiten atribuir el fallo a permisos, controlador u otra causa.

## Verificación

La nueva suite `--test-connection-recovery` comprueba ambos órdenes de llegada (buzón antes/después de Hello), firma alterada, respuesta recibida antes del certificado, conservación de confianza/identidad, índices, callbacks fallidos, cancelación sin negociación QUIC y tres conexiones QUIC nativas entre dos nodos locales, incluyendo un inicio escalonado. Conserva la validación de firmas y la autorización de aplicaciones. La política de transporte se describe en [Transporte automático de peers](../core/peer-transport.md).

Compilación y pruebas se ejecutan con artefactos separados porque QuicNet y Visual Studio mantienen abiertas las DLL del directorio Debug habitual:

```powershell
dotnet build QuicPunchTests/QuicPunchTests.csproj --artifacts-path .codex-build/connection-review-final
dotnet .codex-build/connection-review-final/bin/QuicPunchTests/debug/QuicPunchTests.dll --test-connection-recovery --verbose
```

También se ejecutan las suites de autenticación/rutas UDP (`--test-connectivity-security`), estado de peers, confianza Nostr, multiplexer y cancelación de handshake. La prueba general de aceptación de peers confiables amplía su espera de respuesta de 2 a 10 segundos: el Accept contiene candidatos y el presupuesto configurado del mapeo del router puede superar por sí solo los dos segundos. Esto no cambia el tiempo de espera del protocolo.

Las pruebas de 50 solicitudes concurrentes y reutilización de una sesión activa conceden explícitamente permiso al protocolo ficticio que ejercitan. La confianza del túnel no concede permiso automático a una aplicación; su configuración anterior terminaba con una denegación correcta del core y no comprobaba la deduplicación pretendida.

Resultado final: compilación sin errores y siete suites superadas:

| Suite | Resultado | Tiempo |
| --- | --- | --- |
| `--test-connection-recovery` | OK, incluyendo incorporación a un trabajador entrante ya iniciado | 24,3 s |
| `--test-security` | OK, suite completa | 198,9 s |
| `--test-connectivity-security` | OK | 1,0 s |
| `--test-handshake-cancellation` | OK | 35,8 s |
| `--test-multiplexer` | OK | 11,5 s |
| `--test-peer-state` | OK | 4,8 s |
| `--test-nostr-trust` | OK | 12,8 s |

La última prueba de recuperación establece los tres túneles locales al primer intento en 5,04 s, 5,65 s y 2,88 s; estas cifras excluyen el arranque inicial de los nodos. La compilación conserva avisos previos de nulabilidad y uso de las APIs UDP antiguas en otras pruebas.

Las pruebas locales verifican negociación, TLS QUIC real, arbitraje y sesiones de aplicación. No reproducen los NAT de ambos usuarios, pérdida de paquetes WAN ni instalación de Wintun. Hay que ejecutar la versión recompilada en ambos equipos para comprobar la mejora en esas redes; un proceso ya abierto sigue utilizando sus DLL anteriores.
