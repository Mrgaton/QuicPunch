# Investigación de código muerto y complejidad innecesaria

Fecha: 11 de octubre de 2026. Este documento recoge la investigación previa a los cambios. La ejecución posterior, sus decisiones y validación están en el [informe de implementación](dead-code-cleanup-implementation-2026-10-11.md).

## Dictamen

Sí hay partes cuya retirada beneficiaría al proyecto. La mayor oportunidad está en reducir caminos alternativos que ya no usan las aplicaciones: el canal antiguo de datos UDP, la compresión transparente desconectada del flujo actual y un segundo códec de voz que recibe la configuración pero no codifica el micrófono.

No he encontrado evidencia para justificar borrar subsistemas completos como Tor, Android, Nostr, VPN o el multiplexer. Son complejos, pero tienen consumidores y resuelven necesidades distintas. Tampoco conviene convertir esta limpieza en una reescritura de la conexión o su seguridad.

La limpieza de métodos sueltos aporta claridad, pero por sí sola no cambiará mucho un proyecto de este tamaño. Lo que más ayuda es dejar una sola implementación efectiva para cada responsabilidad.

## Alcance y certeza

Se ha revisado el árbol de trabajo actual, incluidos archivos nuevos y modificaciones pendientes. Git ya contenía numerosos cambios; el informe no parte exclusivamente de HEAD ni interpreta archivos movidos como código muerto.

Inventario de fuentes C# propias, excluyendo bin, obj, runtimes, el árbol excluido Shared y las herramientas históricas de doc: 173 archivos y 59.217 líneas físicas. De ellas, 12.673 corresponden al harness y sus pruebas. Se inspeccionaron también consumidores en JavaScript, proyectos MSBuild y los puntos de integración Android/nativos. No se ha realizado una auditoría exhaustiva del C nativo ni una prueba en un dispositivo Android.

Método: búsquedas de declaraciones y referencias, lectura de llamadores y condiciones de ejecución, contraste con los puntos de entrada y pruebas, y compilación del harness con sus proyectos referenciados. Los recuentos textuales sirven para localizar candidatos; no son una demostración semántica completa de alcanzabilidad.

En una librería pública, «sin consumidores en este repositorio» no equivale a «sin consumidores externos». Las recomendaciones que cambian API pública deben comprobar ese contrato antes de implementarse. Los callbacks del sistema, P/Invoke utilizados, inicializadores de módulo y APIs invocadas por reflexión tampoco se pueden eliminar basándose únicamente en búsquedas de llamadas.

## Decisiones propuestas

| ID | Parte | Decisión | Beneficio | Riesgo/esfuerzo |
| --- | --- | --- | --- | --- |
| H1 | Dos códecs de voz y configuración aplicada al secundario | Unificar en el codificador efectivo | Alto; además corrige comportamiento | Medio |
| H2 | Canal de datos UDP obsoleto | Retirar tras trasladar cobertura | Alto en mantenimiento del protocolo | Medio/alto |
| H3 | Compresión transparente inalcanzable en los flujos actuales | Retirar | Medio; elimina un contrato sin efecto | Bajo/medio por API pública |
| H4 | Helpers sin llamadas, métodos vacíos y campos sobrantes | Retirar por lotes pequeños | Bajo/medio; limpieza segura | Bajo; algunos son públicos |
| H5 | Persistencia de tickets sin reutilización en el nodo | Retirar integración actual | Medio; reduce estado sin beneficio demostrado | Medio por persistencia/API |
| H6 | Adaptadores sin consumidores y fábrica global sustituible | Retirar adaptadores; simplificar fábrica después | Medio/bajo | Medio por API pública |
| H7 | Framing y reensamblado duplicados de datagramas | Consolidar, conservar función | Medio | Medio/alto |
| H8 | Aplicación repetida del tuning nativo | Centralizar responsabilidades | Medio/bajo | Medio |
| H9 | Formato binario WebSocket antiguo sin productor actual | Retirar condicionado a compatibilidad | Bajo/medio | Medio |
| H10 | Prueba huérfana y documentación de funciones retiradas | Recuperar cobertura y corregir documentación | Medio en fiabilidad del mantenimiento | Bajo/medio |

### H1. Unificar el códec que configura la voz con el que realmente la transmite

**Evidencia:** `QuicNet.Shared/Protocols/VoiceCallHandler.cs:23` crea `Codec`. `QuicPunch/Audio/NativeAudioService.cs:61` crea otro `_opusCodec`, que codifica el micrófono con `EncodeFramed` en la línea 208. `VoiceApiModule.cs:434` modifica `_voiceHandler.Codec.Bitrate`; el monitor de QoS de `VoiceCallHandler.cs:510` también modifica ese objeto secundario.

Las únicas llamadas a `Codec.Encode` en VoiceCallHandler están en `SendPcmAsOpusAsync` y `BroadcastPcmAsOpusAsync` —líneas 747 y 753—, que no tienen llamadores en el repositorio. Por tanto, el control de bitrate de la UI y el ajuste adaptativo no modifican el codificador que produce el audio del micrófono en el flujo actual. Es una conclusión del flujo de código; no se ha medido el bitrate de una llamada real en esta investigación.

**Mi decisión:** sí quitaría el códec secundario. Mantendría el códec de NativeAudioService y ofrecería una propiedad de bitrate o una operación de configuración sobre él. Estado, endpoint HTTP y adaptación consultarían y modificarían ese mismo codificador. Eliminaría los dos helpers PCM sin llamadas de la clase interna VoiceCallHandler.

**Por qué beneficia:** desaparece una fuente de estado divergente, una pareja adicional de encoder/decoder y configuración que aparenta actuar sobre la voz pero no lo hace. El beneficio principal es funcional, no el número de líneas.

**Precaución concreta:** la captura y la configuración pueden ejecutarse en hilos distintos. La actualización debe coordinarse con el bloqueo de codificación existente. Como se comparte el micrófono entre peers, varias tareas de adaptación tampoco deben competir sin una política definida; una opción conservadora es usar la necesidad más restrictiva de las llamadas activas. No quitar los decodificadores independientes por peer, que sí tienen función.

**Aceptación:** una prueba con backend de audio simulado debe demostrar que cambiar el bitrate por la ruta de la UI cambia la configuración del encoder que produce el siguiente paquete. Verificar también la adaptación, mute/deafen y el formato secuenciado. Ejecutar las suites de audio, Opus y datagramas, y comprobar una llamada entre dos nodos.

### H2. Retirar el plano antiguo de datos UDP

**Evidencia:** `QuicPunch/QuicPunch.cs:1886` (`GetPacketReader`) y `:4136` (`SendPayloadAsync`) están marcados Obsolete. Las referencias consumidoras localizadas están en `SecurityDiscoveryTests.cs`; no hay llamadas desde QuicNet, QuicNet.Shared o Android. La recepción de `MessageType.Data` continúa activa en `QuicPunch.cs:3767`, con descifrado, ventana antirreplay y publicación en canales. La ruta actual de aplicaciones abre sesiones virtuales del multiplexer en `ExecuteInitQuicConnectionAsync`.

**Mi decisión:** sí lo retiraría del producto actual. No es código totalmente muerto: las pruebas lo ejecutan y la recepción admite paquetes en tiempo de ejecución. Es un protocolo obsoleto sin consumidor de aplicación dentro del repositorio.

**Por qué beneficia:** actualmente se mantienen dos planos de datos de aplicaciones y dos conjuntos de reglas de entrega. El antiguo añade serialización, buffers, canales, índice por ShortId y condiciones de confianza que deben seguir siendo correctas aunque ningún producto lo use. Su retirada reduce la superficie del parser y las combinaciones que hay que mantener.

**Implementación:** trasladar primero las comprobaciones de denegación a peers sin confianza, aislamiento entre peers y entrega al multiplexer y a los datagramas actuales. Después eliminar las dos APIs, `_packetChannels`, `PublishReceivedData`, el bloque receptor de Data y el índice `_peersByShortId` con sus altas/bajas, cuyo único lookup de producción está en ese bloque. Conservar una comprobación que los paquetes Data antiguos no entregan datos de aplicación. Evitar renumerar o reutilizar el discriminante de wire accidentalmente.

**Límite importante:** no borrar de golpe ECDH, nonces, derivación de sesión ni todos los campos de cifrado de PeerInfo. `IsSessionReady` depende hoy de ellos y se utiliza para admitir peers y preparar QUIC. Separar ese estado de autenticación sería otro cambio de protocolo, con revisión propia. Tampoco retirar protecciones de repetición de mensajes de control.

**Aceptación:** no quedan consumidores ni advertencias de estas APIs; pasan seguridad, permisos, transporte automático, recuperación, cancelación de handshake, multiplexer y datagramas. Si la librería se distribuye a otros proyectos, la eliminación debe tratarse como cambio de API incompatible. Las pruebas de cifrado que sigan validando el handshake deben conservarse; las exclusivas del protocolo retirado se sustituyen o retiran de manera explícita.

### H3. Eliminar la compresión transparente desconectada

**Evidencia:** las dos llamadas a `InitQuicConnectionCore`, en `QuicPunch.cs:3399` y `PacketHandler/HandshakeHandler.cs:300`, pasan `null` como compressionOptions. El único uso de `CompressedTransparentStream` está en el bloque condicionado a un valor no nulo de `QuicPunchConnection.cs:231`. Además, todos los handlers actuales devuelven `CompressionOptions => null`, y no se encuentra una lectura de esa propiedad que la aplique a las sesiones virtuales.

**Mi decisión:** sí retiraría `Helpers/CompressedTransparentStream.cs` —116 líneas—, el parámetro interno, el bloque condicionado y la propiedad de IProtocolHandler junto con sus implementaciones. Si se quiere compresión en el futuro, deberá decidirse en el protocolo de aplicación y negociarse con el remoto.

**Por qué beneficia:** se elimina un contrato que obliga a todos los handlers a implementar una configuración que no se utiliza, y un wrapper bidireccional con lógica de flush y disposición que no participa en el flujo actual. No hay pérdida de compresión operativa en las aplicaciones revisadas. La eliminación del tipo y de la propiedad públicos sí puede afectar a consumidores externos.

**Aceptación:** compilan los handlers, mocks y proyectos Android/Desktop; las búsquedas no encuentran el contrato retirado. No hace falta inventar pruebas de compresión para una función que se está eliminando. Corregir su descripción en `doc/core/helpers-and-utilities.md`.

### H4. Borrar restos pequeños con evidencia directa

Estos casos tienen alta certeza dentro del repositorio. Los miembros públicos de la librería siguen sujetos a la comprobación de consumidores externos indicada antes.

| Fuente | Restos | Evidencia y decisión |
| --- | --- | --- |
| `QuicNet/Protocols/VirtualLanHandler.cs:554` y `:588` | `SetAdapterIP`, `SetAdapterMTU` | Sin llamadas; la ruta activa utiliza `_adapter.SetIpAddress` y `_adapter.SetMtu`. Borrar estos aproximadamente 66 renglones, conservando IVpnAdapter y sus implementaciones por sistema. |
| `QuicPunch/Audio/NativeAudioService.cs:166` y `:171` | `EnqueueScreenAudio`, `TransmitScreenAudioDirectly` | Cuerpos vacíos y sin llamadas. Borrar; no aportan procesamiento ni transmisión. |
| `QuicPunch/Audio/PulseAudioBackend.cs:51` | `PaSimpleNewNoAttr` | Declaración P/Invoke privada sin llamadas. Se utiliza la variante PaSimpleNew con atributos; borrar únicamente la variante sobrante. |
| `QuicPunch/QuicPunchBuilder.cs:24` y `:151` | Campo `_cts` | Se asigna, pero no se lee. Build utiliza `_cancellationToken`. Borrar campo y asignación; conservar los métodos de configuración que sí transmiten el token. |
| `QuicPunch/QuicPunch.cs:4116` | `_ = PreciseTime.WaitNextTrigger(delayMs)` | Crea una espera descartada; el bucle se retrasa después con Task.Delay. Borrar la llamada evita una tarea/timer que no coordina la siguiente iteración. No retirar GetCorrectTime ni su función en autenticación temporal. |
| `QuicPunchTests/Program.cs:118` | `InspectMsQuicParams` | Método privado sin llamadores. El switch llama directamente a TestQuicCongestionControlTuning. Borrar el wrapper. |
| Program de QuicNet y de QuicPunchTests | `CurrentProcess`, `FileName` | Campos sin consumidores. Borrar inicialización y campos si la comprobación final confirma el mismo estado. |
| Handlers de aplicaciones | `PreferredPort => 0` | Ya no forma parte de IProtocolHandler y no tiene lectores. Retirar estos miembros de implementación, sin modificar la selección de puertos de QUIC. |
| `QuicNet.Shared/Protocols/ChatHandler.cs:371` | `SendDirectQuicMessageAsync` | Método de la clase interna sin llamadores. No es la ruta del endpoint actual de chat; retirar únicamente el helper. |

**Por qué beneficia:** reduce vías duplicadas, APIs engañosas y ruido de lectura. El ahorro global es modesto. No atribuir a esta fase mejoras grandes de rendimiento.

**Aceptación:** búsqueda final de referencias y compilación de los proyectos afectados. Para estos borrados triviales no se necesitan pruebas nuevas que reproduzcan la implementación.

### H5. Retirar la integración de tickets que no acelera reconexiones

**Evidencia:** `QuicPunchConnection.cs:70` carga un ticket desde PeerStore a PeerInfo; `:175` intenta extraer y guardar otro tras conectar. `TryRunClient`, en `:704`, crea opciones y conecta sin consumir el ticket cargado. `QuicConnection.TrySetResumptionTicket` no tiene llamadores. El test de `NativeQuicOptimizationsTests.cs:267` asigna un ticket simulado y prueba recuperación/persistencia; no demuestra una reconexión que use ese ticket.

**Mi decisión:** retiraría la lectura y la persistencia automáticas del flujo del nodo, porque no hay beneficio demostrado de reconexión en la implementación actual. No propondría «completar 0-RTT» dentro de esta limpieza: sería una funcionalidad nueva con requisitos propios.

**Por qué beneficia:** evita estado replicado entre conexión, PeerInfo y PeerStore, y escrituras de tickets cuyo valor no se usa para conectar. No se afirma que las APIs nativas de resumption carezcan de utilidad; la integración actual del nodo es la que está incompleta.

**Implementación:** primera fase, retirar únicamente la integración en QuicPunchConnection. Segunda fase, decidir si se conserva una API de bajo nivel intencionada; si se elimina también el campo de persistencia, los peers guardados antes deben seguir cargando sin pérdida de identidades, permisos ni endpoints. Eliminar los tests de almacenamiento de tickets sólo si se retira ese contrato.

**Aceptación:** recuperación y reconexión siguen funcionando; abrir una base anterior conserva los peers y su política de confianza. No cambiar los certificados ni el pinning.

### H6. Reducir las extensiones de transporte que no se usan

**Evidencia:** `Tor/TorQuicConnectionFactory.cs` —54 líneas— y `Tor/DelegateDummyQuicLaneProvider.cs` —53 líneas— no tienen consumidores localizados, tampoco en tests. Tor se integra directamente con TorQuicConnectionManager y CreateDummy. La propiedad mutable `QuicConnection.TransportFactory` sólo se lee en la conexión normal; no se encuentra un cambio de fábrica por los productos o las pruebas. UseNativeTransport y la sobrecarga que recibe una fábrica tampoco tienen consumidores localizados.

**Mi decisión:** retiraría los dos adaptadores si no forman parte de una API externa comprometida. En un segundo lote, simplificaría la selección global hacia el transporte nativo fijo; no eliminaría IQuicConnectionTransport ni IDummyQuicLaneProvider, que sí tienen implementaciones y uso efectivo en WAN, Tor, Android y sesiones virtuales.

**Por qué beneficia:** hay menos puntos de extensión que aparentan necesitar soporte. Retirar el estado global mutable también evita que una sustitución afecte a todos los nodos del proceso. No hay evidencia de que estas 107 líneas sean una causa de complejidad extrema; su prioridad es inferior a H1-H3.

**Aceptación:** conexión nativa, introspección, mock de un solo stream, Tor y Android conservan su ruta. No confundir el adaptador sin consumidores con el gestor de Tor activo.

### H7. Consolidar el tratamiento de datagramas, sin eliminarlo

**Evidencia:** `Tor/NativeConnectionTransport.cs:154` y `Multiplexer/MultiplexedQuicConnectionTransport.cs:90` implementan por separado secuencias, framing, fragmentación, categorías y límites de MTU. Los dos receptores también interpretan envelopes y mantienen reensambladores. Ambos caminos tienen consumidores y pruebas.

En la ruta del nodo, el multiplexer se suscribe a los datagramas crudos del transporte físico (`QuicPeerMultiplexer.cs:126`). El transporte nativo también procesa cada paquete en OnRawDatagramReceived y construye un PeerInfo (`NativeConnectionTransport.cs:121`). Hay trabajo adicional en ese nivel aun cuando el protocolo de aplicación se entrega desde la sesión virtual. No se ha cuantificado su coste.

**Mi decisión:** consolidar el framing/fragmentación común en una implementación concreta pequeña, reutilizada por conexión nativa y virtual. Conservar reensamblado y secuencias independientes por contexto. Revisar que la ruta física del nodo no decodifique el encabezado de sesión del multiplexer como si fuera un envelope de aplicación; el API directo de datagramas nativos debe seguir funcionando.

**Por qué beneficia:** los arreglos de MTU, pérdida, fragmentos y límites no deben mantenerse dos veces. La función es necesaria; lo sobrante es la duplicación y el procesamiento en un nivel que no sea el dueño del formato.

**Aceptación:** pasan datagramas crudos, categorizados y multiplexer; verificar límites de payload en sesiones virtuales, fragmentos fuera de orden o duplicados, aislamiento de sesiones, cierre y limpieza. Comparar asignaciones y rendimiento antes de afirmar una mejora. No introducir una jerarquía genérica extensa para ahorrar unas decenas de líneas.

### H8. Aplicar el tuning nativo en un punto definido

**Evidencia:** la ruta de cliente aplica TryApplyOptimalTuning en NativeQuicConnectionFactory (`Tor/QuicConnection.cs:657`), vuelve a hacerlo al construir el wrapper (`:315`), después en TryRunClient (`QuicPunchConnection.cs:750`) y otra vez en InitQuicConnectionCore (`:171`). TryApplyOptimalTuning vuelve a revisar la configuración global y ejecuta ajustes nativos; no es un simple getter.

**Mi decisión:** conservar la configuración previa al handshake cuando corresponda y una aplicación por conexión después de crear/aceptar el transporte. Retirar las reaplicaciones idénticas en capas superiores. Mantener ajustes posteriores intencionados de prioridad, DSCP o congestión.

**Por qué beneficia:** aclara qué capa es dueña de los defaults y evita llamadas nativas repetidas o la restauración accidental de valores que otra capa acaba de ajustar. No eliminar MsQuicTuner: telemetría, datagramas y QoS sí lo utilizan.

**Aceptación:** validar conexión entrante/saliente y las rutas ConnectAsync/Wrap, BBR, MTU y datagramas. Un test específico debe comprobar que los ajustes intencionados no se sobrescriben por una inicialización posterior. Evitar retirar la configuración previa al handshake simplemente porque se repita su nombre.

### H9. Retirar compatibilidad WebSocket antigua si no existe contrato con clientes anteriores

**Evidencia:** `WebUiWebSocketHub.cs:321` interpreta un formato de audio antiguo sin etiqueta. `BroadcastBinaryAudio` en `:353` no tiene llamadas. El JS actual produce vídeo con etiqueta 0x02 y audio de pantalla con 0x03; la voz nativa sale de NativeAudioService.

**Mi decisión:** eliminar ya el helper sin llamadas; retirar el fallback de entrada sin etiqueta sólo al confirmar que no se soportan frontends antiguos o clientes externos. Considerar la entrada etiquetada 0x01 bajo el mismo criterio, no como código muerto demostrado. Mantener 0x03/0x04, que transportan audio de pantalla activo.

**Por qué beneficia:** un único formato explícito evita interpretar el primer byte de un GUID como etiqueta de medio y reduce combinaciones de parser. Ser antiguo no convierte el fallback en inalcanzable: una petición de un cliente todavía lo puede ejecutar.

**Aceptación:** pruebas de binarios WebSocket: etiquetas válidas, tamaños mínimos, etiqueta desconocida, destinos y audio de pantalla. Pasan las pruebas web de vídeo y seguridad del WebUI.

### H10. Recuperar una prueba huérfana y retirar documentación engañosa

`SecurityDiscoveryTests.cs:3850` contiene `TestReconnectingDiscoveredPeerWhenEndpointChanges`, que no se llama desde RunAsync ni desde otro punto localizado. **No la borraría por ser código muerto**: valida un caso relevante. Revisaría el fixture y la incorporaría a la suite adecuada, o demostraría cobertura equivalente en ConnectionRecoveryTests antes de retirar la duplicada. La prueba puede haber quedado desactivada porque espera una política antigua; no hay evidencia suficiente para asumir que basta con añadir una llamada.

La documentación describe `RelayDriveHandler`, cuyo archivo no existe en el árbol actual (`doc/protocols/relay-drive-protocol.md:3`), y el README anuncia RelayDrive. Los archivos y medios actuales se sirven por Chat/Watch. También hay contratos PreferredPort y referencias históricas a handlers que se movieron. Corregirlos evita mantener mentalmente funciones que ya no están. No retirar transferencia de archivos ni Watch por una referencia documental obsoleta.

## Partes que conservaría

| Parte | Motivo para conservarla |
| --- | --- |
| Multiplexer y sus interfaces de conexión/stream | Permiten compartir una conexión física entre varias aplicaciones y soportan implementaciones nativa, Tor, Android y virtual. |
| TorPeerTransportHub / TorQuicConnectionManager | Tienen uso en el flujo Tor y una función distinta del adaptador sin consumidores de H6. |
| STUN y cascada PCP/NAT-PMP/UPnP | Son mecanismos distintos de conectividad; no se ha demostrado que uno haga redundantes a los otros. |
| Límites, generaciones, cancelación y coordinación de handshakes | Protegen reinicios, carreras y recursos. Las suites de recuperación/seguridad se apoyan en esos comportamientos. |
| Nostr discovery y mailbox persistente | Descubrimiento y mensajes offline son funciones distintas con consumidores efectivos. |
| Android y NativeBuilder/PQC | Forman parte de los proyectos y del empaquetado actuales. Simplificarlos exigiría una decisión de producto, no una limpieza de código muerto. |
| Árbol QuicPunch/Shared | Está excluido explícitamente del proyecto y Git lo registra como enlace. No justificaría borrarlo por falta de referencias en el ensamblado principal. |
| Builder y APIs públicas pequeñas sin llamadas locales | Son puntos de consumo de una librería; el propio README utiliza el builder. Ausencia de llamadas internas no demuestra falta de utilidad. |

El tamaño de QuicPunch.cs —4.305 líneas— dificulta la lectura, pero dividirlo en archivos no elimina complejidad lógica. Después de retirar H2/H3, se puede extraer la responsabilidad del mailbox o de sesiones a clases concretas; no haría esa reorganización en el mismo cambio que modifica los protocolos.

## Plan de implementación

1. **Limpieza de bajo riesgo: H4 y parte de H10.** Un commit con miembros internos/privados sin consumidores, métodos vacíos y campo no leído; otro con documentación. Revisar por separado las APIs públicas. No mezclar cambios de comportamiento. Criterio de cierre: referencias y builds limpios respecto a lo retirado.
2. **Eliminar compresión desconectada: H3.** Cambiar IProtocolHandler, los handlers, mocks y parámetros internos juntos. Documentar el cambio de contrato si hay consumidores externos. Criterio de cierre: no existe configuración de compresión sin efecto; builds de desktop y Android.
3. **Unificar audio: H1.** Añadir configuración al servicio que codifica, migrar UI/QoS y eliminar el códec secundario y sus helpers. Introducir la prueba que conecta configuración con encoder efectivo. Criterio de cierre: bitrate mostrado/configurado y el del micrófono pertenecen al mismo codificador.
4. **Retirar datos UDP antiguos: H2.** Primero cobertura del comportamiento equivalente actual, después eliminación de APIs/parser/canales/índice. Mantener el handshake actual. Criterio de cierre: la aplicación tiene un único plano de datos operativo y siguen pasando las pruebas de confianza y recuperación.
5. **Reducir estado y extensiones sin efecto: H5/H6.** Separar integración de tickets, migración de almacenamiento y API de fábrica en cambios pequeños. Criterio de cierre: reconexión, peers persistidos y Tor siguen funcionando sin estado nunca utilizado.
6. **Consolidar transporte: H7/H8.** Hacerlo con la línea base anterior estabilizada, una responsabilidad por cambio. Criterio de cierre: mismos formatos y comportamiento observable, sin duplicación de framing ni reaplicaciones de defaults.
7. **Cerrar compatibilidad WebSocket: H9.** Ejecutar después de resolver si se mantienen clientes anteriores. El helper sin llamadas puede ir en el primer lote. Criterio de cierre: parser y productores comparten un formato inequívoco.

No usaría un objetivo de «eliminar X % del proyecto»: no refleja seguridad ni facilidad de mantenimiento. La estimación prudente es eliminar cientos de líneas y varios contratos/estados, no miles de líneas de funcionalidad sin consecuencias. Las fases 1-2 tienen menor riesgo; 3-4 ofrecen mayor beneficio; 6 tiene más posibilidad de regresión.

## Verificación realizada

| Comprobación sobre el código actual | Resultado |
| --- | --- |
| `dotnet build QuicPunchTests/QuicPunchTests.csproj --artifacts-path .codex-build/dead-code-audit -v:quiet` | Correcto: 0 errores, 28 warnings. Incluye proyectos referenciados de desktop/core/VPN/shared/NativeBuilder; no compila Android. |
| `--test-multiplexer` con el harness de esa compilación | OK, 12,6 s. |
| `--test-datagram-categorization` con el mismo harness | OK, 12,7 s. |
| `node QuicPunchTests/Tests/PeerConnectionStateWebTests.mjs` | PASS. |
| `node QuicPunchTests/Tests/ScreenVideoWebTests.mjs` | PASS. |

La primera compilación en bin/obj habituales no pudo copiar DLLs usadas por QuicNet/Visual Studio. La compilación aislada anterior resolvió ese bloqueo; no se cerraron los procesos del usuario. Los warnings de la compilación aislada son de nulabilidad y uso de APIs UDP obsoletas.

Los logs de build/multiplexer/datagramas están en `.codex-build/dead-code-audit-*.log`. Estas comprobaciones establecen una línea base limitada: **no validan cambios todavía no implementados**, no cubren todas las suites y no sustituyen validación WAN, Linux, Tor real o Android en dispositivo cuando se modifiquen esas rutas.
