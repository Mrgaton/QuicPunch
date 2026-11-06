# Correcciones de los seis hallazgos P2P

Fecha: 22 de octubre de 2026. Implementación solicitada después de entregar la [auditoría de análisis](informe.md). Se corrigieron primero F01–F04 y después F05–F06. El informe y sus reproducciones originales se conservan como evidencia del estado anterior.

## Resultado

Los seis hallazgos tienen corrección y regresión satisfactoria. La compilación `Test` termina sin errores; pasan diez suites C# relacionadas y los 19 archivos de pruebas web. Esto valida los casos locales ejecutados; no constituye una certificación de todo el producto ni una prueba de rendimiento en redes públicas.

| Hallazgo | Implementación | Regresión observada |
| --- | --- | --- |
| F01: trabajo ilimitado al servir medios | Admisión por sesión, cuatro workers y presupuesto global antes de invocar el servicio que abre el archivo | Receptor bloqueado, duplicados, 1.000 solicitudes, seis peers y devolución de reservas al desconectar |
| F02: cachés sin presupuesto ni retirada | Chunks verificados en memoria, cuotas, LRU y retirada al cambiar/cerrar/salir de sala | 1.000 anuncios, múltiples orígenes, 1.000 cambios mediante el coordinador, cancelación de lectores y limpieza heredada |
| F03: destrucción del semáforo con escritores pendientes | Vida cancelable por sesión, cierre idempotente del stream y espera de workers; puertas administradas sin destrucción mientras existen usuarios | EOF y reemplazo terminan con cero respuestas activas y el origen admite `FileShare.None`; cancelación conserva el protocolo utilizable |
| F04: invitación Tor comparada con identidad WAN | Resolución por canal autenticado y `LocalPeerId` de sala, aplicada a propiedad, participantes, permisos, control, selección y salida | Invitación WAN válida y negativas de sender/target; frames reales por TCP local con identidades Tor distintas de WAN, permisos, playback y salida |
| F05: Flush concurrente con escritura | Un único consumidor posee el escritor y procesa barreras de Flush, rotación y directorio | IO retenida, dos barreras, saturación, rotación, cambio de directorio, cierre y fallo de escritura inyectado |
| F06: cuatro pruebas web desactualizadas | Fixture que carga los helpers reales, carga completa de chat y presupuesto de avatar de 512 KiB | 19/19 archivos web; regresiones del servidor de avatar incluidas en WebUI/security |

## F01 y F03: admisión y vida de sesión

[MediaTransferHandler](../../../QuicNet.Shared/Watch/Protocols/MediaTransferHandler.cs) utiliza estos límites:

| Recurso | Límite |
| --- | --- |
| Solicitudes de servicio admitidas por sesión | 20 en total; cuatro workers y hasta 16 pendientes cuando los cuatro trabajan |
| Callbacks de servicio simultáneos en el handler | 16 globales, con admisión anterior a abrir el archivo |
| Rangos recibidos simultáneamente | Cuatro por sesión y 16 globales; admisión anterior al buffer del rango |
| Longitud de rango | 1 MiB |
| Frame entrante | 128 KiB |
| Bloque de datos de servicio | 64 KiB |
| Plazo de un trabajo de servicio o petición saliente | 30 segundos desde su ejecución/admisión correspondiente |
| Plazo de escritura de un frame | 10 segundos |

No se crea una tarea por cada Request entrante. La admisión contabiliza los RequestId antes de encolarlos; un duplicado todavía admitido no crea otro trabajo. Si la sesión supera su capacidad, se rechaza cerrando esa sesión virtual. Se conservan los frames existentes Request/Data/Complete/Cancel: no se introduce un Busy incompatible ni se cierra la conexión compartida de otros protocolos.

Cancel cancela también el trabajo que sirve el rango. Un frame admitido se escribe íntegro o se detiene la sesión si la escritura falla; cancelar un rango entre frames no debe truncar el encuadre. Las excepciones de servicio se observan y se contesta con Cancel mientras la sesión siga viva.

Cada sesión posee CTS, registro de trabajos y workers. EOF, cancelación, sustitución o Dispose detienen admisiones, cancelan, cierran el stream y esperan los workers. Las puertas administradas se dejan al GC, porque las llamadas externas a la API pueden estar saliendo de WaitAsync/Release. El contexto de respuesta conserva la sesión original para impedir que un trabajo antiguo escriba en una sustituta. Se aplica también la retirada del Dispose prematuro de la puerta de escritura de [WatchControlHandler](../../../QuicNet.Shared/Watch/Protocols/WatchControlHandler.cs).

Las pruebas usan el servicio de archivo real y un stream con escritura bloqueada. Tras EOF y tras sustitución, el archivo puede abrirse de forma exclusiva dentro del plazo de cinco segundos. En la prueba global se cuentan tanto las reservas como los callbacks realmente activos: máximo 16 y cero después de desconectar.

## F02: presupuesto de caché y ciclo de vida

[MediaLibrary](../../../QuicNet.Shared/Watch/Services/MediaLibrary.cs) deja de preextender un archivo del tamaño anunciado. Un anuncio válido solo reserva metadatos; los bytes se descargan, verifican y almacenan al solicitar un rango.

| Recurso | Límite |
| --- | --- |
| Tamaño de medio, local o remoto | 64 GiB |
| Entradas de caché | Ocho globales, cuatro por origen |
| Payload retenido por entrada | 4 MiB y 1.024 chunks como máximo, con LRU |
| Payload retenido total | 32 MiB globales; 16 MiB por origen |
| Archivos/handles de caché nuevos | Cero: la caché nueva es temporal en memoria |

Los límites de payload retenido no son un límite de RSS del proceso: existen además buffers transitorios de HTTP, transferencia, integridad y lectura. Los tamaños de rango y la concurrencia de transferencia también están acotados. Las lecturas de una caché se serializan para evitar descargas duplicadas y carreras en sus chunks.

Se expulsan entradas inactivas por LRU antes de admitir otra. Si las entradas necesarias para liberar cuota tienen lectores, la admisión falla en vez de superar la cuota. Un mismo MediaId no puede reasignarse a otro manifiesto. Los lectores, incluidos los que esperan, mantienen una referencia; retirar un medio los cancela y aplaza la destrucción hasta que todos salgan.

[RoomCoordinator](../../../QuicNet.Shared/Watch/Services/RoomCoordinator.cs) retira medios anteriores al cambiar el vídeo, al salir y al recibir room_closed. Dispose retira todo. El inicio elimina únicamente los archivos heredados con nombre `GUID.cache` en la carpeta propia que pueden abrirse en exclusiva; conserva archivos ajenos y omite archivos inaccesibles o en uso. No se crean nuevos archivos cuya inicialización pueda fallar por falta de espacio.

La elección de memoria temporal sigue la alternativa de caché de sala del informe. No hay persistencia nueva ni descarga de todo el vídeo. La caché antigua tampoco persistía un índice de chunks verificados entre reinicios. Los vídeos de más de 64 GiB reciben un error explícito; cambiar ese máximo requiere revisar el presupuesto y las pruebas.

La regresión transfiere un archivo real de seis MiB mediante ambos extremos del protocolo por TCP local, comprueba integridad, reutilización LRU y lecturas que cruzan chunks. Retirar una caché cancela al lector activo y al que espera, propaga Cancel al origen y permite una nueva petición válida. La revocación del permiso y un offset fuera del archivo reciben rechazo, tras lo cual la sesión sigue sirviendo rangos válidos. Además de las cuotas de biblioteca, se ejecutan 1.000 cambios mediante el coordinador: queda una sola entrada de metadatos y cero bytes descargados. room_closed y Leave dejan cero entradas.

## F04: identidad por transporte y sala

La identidad local se resuelve a partir del peer del canal de control autenticado y del proveedor de identidad adjunto al protocolo. [WatchRoom](../../../QuicNet.Shared/Watch/Domain/WatchModels.cs) almacena LocalPeerId como estado local; no se añade un identificador WAN al formato Tor. [WatchWebServer](../../../QuicNet.Shared/Watch/Web/WatchWebServer.cs) usa la identidad de la sala para el estado local y para seleccionar el origen del medio.

Las invitaciones siguen comprobando SenderPeerId contra el peer autenticado y TargetPeerId contra la identidad local de ese canal. WAN no es un destinatario aceptado en un canal Tor. La propiedad, los participantes, permisos, control y salida usan el mismo ámbito. Las operaciones de sala rechazan un cambio de ámbito del canal para evitar reutilizar el estado con otra identidad.

Se ha elegido un único ámbito por sala. Crear o ampliar una sala con mezcla WAN/Tor produce «Crea salas separadas para WAN y Tor». Una sala creada sin participantes toma WAN; para una sala Tor se seleccionan participantes Tor al crearla. Definir salas mixtas con traducción de identidades queda fuera de esta corrección.

La nueva prueba ejecuta ambos coordinadores y handlers de control reales sobre TCP local con identidades Tor distintas de WAN. La suite de privacidad Tor adicional comprueba los IDs usados en los mensajes de los protocolos y que los peers directos conservan WAN. No se ha ejecutado esta sesión con dos servicios onion públicos.

## F05: escritor único y contrato de Flush

[RotatingFileLogger](../../../QuicPunch/Helpers/RotatingFileLogger.cs) utiliza un canal acotado de órdenes: datos, Flush, rotación y cambio de directorio. Solo el consumidor manipula el StreamWriter. Flush completa después de escribir y vaciar al stream los datos admitidos anteriores; no ofrece fsync ni garantiza persistencia física ante corte de alimentación.

La capacidad predeterminada sigue siendo 10.000, con mínimo 100. Write mantiene admisión no bloqueante. Al saturarse descarta el mensaje nuevo, en lugar de expulsar órdenes anteriores como podía ocurrir con DropOldest. Las órdenes de control esperan capacidad y nunca son descartadas por saturación. Dispose completa el canal y espera su drenaje.

Un fallo del worker hace fallar las barreras pendientes y cierra el canal, de modo que no quedan Flush esperando indefinidamente. La regresión conserva una escritura de 128 KiB pendiente, encola dos Flush, satura la cola, pide rotación y cambio de directorio, libera la IO y comprueba contenido y drenaje. Un segundo caso inyecta IOException mientras hay dos barreras y un Dispose: ambas barreras fallan con la excepción esperada y el cierre termina. La línea «Worker error: Injected disk write failure» del log corresponde a ese fallo intencional.

## F06: pruebas web y política de avatar

El nuevo [WebAppRuntime](../../../QuicPunchTests/Fixtures/WebAppRuntime.mjs) carga app.js en el VM y conserva sus implementaciones reales de peerInConnectionMode y trusted; las pruebas mantienen dobles de DOM/IO. Se comprueba la precedencia de desiredEnabled y la selección WAN/Tor. ChatNavigation carga chat.js completo con sus funciones y estado, sin cortar por una coincidencia textual.

El presupuesto de avatar continúa siendo 512 KiB, que ya usaban cliente, worker y servidor. Se actualizan expectativas, mensajes de pruebas y [documentación de perfiles](../../webui-and-apps/profile-photos.md). El límite de 320 píxeles de las animaciones es independiente y se conserva. La suite WebUI/security ejecuta también las comprobaciones de avatar del servidor, incluido el rechazo por encima del máximo.

## Verificación y reproducción

Compilación usada: SDK .NET 11 rc.2 en Windows x64, configuración Test, dependencias nativas locales. La compilación final termina con cero errores y seis CS8602 preexistentes en SecurityDiscoveryTests; no se han modificado esas advertencias.

Comandos permanentes desde la raíz:

```powershell
dotnet build QuicPunchTests/QuicPunchTests.csproj -c Test
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-watch-reliability --loopback-only --verbose
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-rotating-logger --loopback-only --verbose
& scripts/test.ps1 -Web -NoBuild
```

El flag de suite debe ser el primer argumento. Se amplió la lista permitida de --loopback-only para estas dos suites. La prueba de Watch utiliza streams controlados y TCP en loopback; no inicia Tor público ni necesita gateways externos.

| Suite ejecutada | Resultado |
| --- | --- |
| --test-watch-reliability | OK; incluye las nuevas regresiones F01–F04 |
| --test-rotating-logger | OK; incluye barreras y fallo de disco inyectado |
| --test-webui-security | OK; HTTP, seguridad, avatar y aislamiento de salas |
| --test-tor-privacy | OK |
| --test-critical-reliability | OK |
| --test-resumable-files | OK |
| --test-connection-recovery | OK |
| --test-session-recovery | OK |
| --test-aot-json | OK; 231 contratos |
| --test-updates | OK |
| scripts/test.ps1 -Web -NoBuild | 19/19 OK |

Las diez suites C# se ejecutaron con el fixture de aislamiento de la auditoría, que fija reloj de sistema, remapea descubrimiento/STUN a loopback y bloquea HTTP no local mediante proxy. Para las suites sin --loopback-only habilitado se utilizó el harness documentado en [reproduccion.md](reproduccion.md), sin ejecutar sus reproducciones históricas de fallos. Tras la última modificación del protocolo se repitieron Watch, logger, WebUI/security y privacidad Tor; todas terminaron con salida 0.

Los logs regenerables están en `.artifacts/p2p-fixes-2026-10-22/`: `build-final.log`, `final-watch-reliability.log`, `final-rotating-logger.log`, `final-webui-security.log`, `final-tor-privacy.log`, `regression-*.log` y `web-final.log`. Este documento conserva los resultados esenciales para no depender de esa carpeta desechable.

No se repitió una compilación Android: el entorno de la auditoría carecía del workload necesario. Tampoco se cerraron aplicaciones del usuario para liberar binarios Debug bloqueados. No se han medido throughput, búsquedas de vídeo o calidad de reproducción en WAN/Tor públicos; los nuevos límites pueden reducir paralelismo y reutilización de caché, por lo que esos ensayos siguen siendo necesarios antes de evaluar rendimiento de producción.

## Convivencia con otras tareas

Se conservaron los cambios locales anteriores y los archivos ajenos a estas correcciones. No se restauró el árbol a HEAD ni se hicieron reset, stash, cambio de rama o commit. Se tomaron copias temporales de los ocho archivos principales antes de editar y se contrastaron contra ellas, porque el diff con HEAD incluye trabajo previo del usuario y otras tareas. Por ejemplo, Program.cs conserva su ejecutor y suites existentes; esta corrección solo añade el flag de Watch, su ayuda y la admisión de las dos suites locales en la lista de aislamiento.

La documentación técnica actualiza la política de caché, identidad, logger y avatar. El informe original sigue describiendo la evidencia anterior a la corrección y enlaza esta actualización. Los asuntos no confirmados N01–N02 y las limitaciones de cobertura originales no se declaran resueltos por este trabajo.
