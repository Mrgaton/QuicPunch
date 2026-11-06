# Auditoría de fiabilidad y seguridad P2P de QuicPunch y QuicNet

> Documento histórico del estado anterior a las correcciones. Los seis hallazgos se han corregido posteriormente por petición del usuario; implementación, regresiones y límites en [correcciones.md](correcciones.md).

Fecha: 22 de octubre de 2026. Modalidad: análisis, pruebas locales y documentación; sin correcciones en el código del producto.

## 1. Resumen ejecutivo

**Se confirman seis problemas: dos de gravedad alta, tres de gravedad media y uno de gravedad baja.** Los dos problemas altos permiten que un contacto autorizado para QuicWatch provoque crecimiento de recursos sin un presupuesto de aplicación. También se confirma una fuga de trabajos y archivos abiertos al cerrar sesiones de medios, el rechazo de invitaciones QuicWatch dirigidas a la identidad Tor, una carrera de escritura en el logger y cuatro pruebas web incompatibles con el código que prueban.

El transporte central cuenta con defensas relevantes y comprobaciones que pasan: identidad por certificado, pinning, separación entre descubrimiento y confianza, autorización por protocolo, límites de sesiones, recuperación de conexiones y verificación de archivos. Se negoció realmente X25519MLKEM768 en QUIC local. No se ha confirmado una vía para que un desconocido omita esas autorizaciones o ejecute código arbitrario.

**No recomiendo considerar listo para producción el conjunto completo con QuicWatch habilitado antes de resolver F01–F04.** F01 y F02 afectan a disponibilidad y recursos del equipo que ejecuta la aplicación. F03 persiste después del cierre de una sesión. F04 impide una función ofrecida sobre Tor. F05 debe corregirse para que la API de vaciado de registros sea fiable. F06 impide utilizar la batería web actual como comprobación completamente satisfactoria.

Esta conclusión no equivale a rechazar por completo la biblioteca de transporte: las reproducciones principales se concentran en la capa de aplicación QuicWatch. Tampoco equivale a certificar el resto del producto. La matriz de cobertura distingue ejecución, inspección y límites pendientes.

### Estado auditado y cambios concurrentes

El HEAD de referencia inicial era `284b41d1b20e7ff5c4f8050a915075742c92baf8`, pero **los resultados corresponden al árbol de trabajo con cambios locales**, no al contenido de ese commit. Al inicio había 302 entradas en `git status --porcelain`.

Se tomaron hashes iniciales de 409 archivos de código, pruebas, configuración y scripts. Durante la auditoría cambiaron ocho de esos archivos por actividad ajena a las ediciones de esta auditoría: `QuicPunch.cs`, `AppNotificationService.cs`, `JsonContracts.AppSettings.Context.cs`, `AppPreferencesStore.cs`, `AppSettingsApiModule.cs`, `PeerApiModule.cs`, `AppBehaviorBrowserTests.mjs` y `NotificationTests.cs`. Aparecieron además pruebas nuevas de identidad y privacidad Tor. No se restauraron ni sobrescribieron esos cambios.

Los archivos donde residen F01–F05 y las cuatro pruebas de F06 **no cambiaron respecto de sus hashes iniciales** durante la comprobación de los hallazgos. Los ensayos generales se ejecutaron en momentos distintos de un árbol activo; no representan una certificación de una instantánea inmutable de todo el repositorio. El anexo de evidencia conserva hashes y resultados para delimitarlo.

La auditoría solo añadió esta documentación y materiales temporales de diagnóstico bajo `.artifacts/p2p-audit-2026-10-22/`. Las compilaciones usaron `bin/` y `obj/`. No se aplicó ninguna corrección al producto ni se alteraron identidades de publicación.

## 2. Arquitectura reconstruida

### Proyectos y dependencias

| Componente | Implementación observada | Responsabilidad |
| --- | --- | --- |
| `QuicPunch` | C#, `net11.0`, código nativo y acceso a MsQuic | Fachada, descubrimiento, autenticación, negociación, multiplexación, datagramas y transporte Tor |
| `QuicPunch.NativeBuilder` | C#, parches de MsQuic/OpenSSL y manifiesto de compilación | Biblioteca nativa local y configuración de grupos TLS |
| `QuicNet.Shared` | C#, `net11.0` | Protocolos, WebUI, chat, archivos, voz, presencia, actualizaciones, disco virtual y servicios comunes |
| `QuicNet` | C#, `net11.0`, Photino y HTML/JS/CSS | Aplicación de escritorio y VPN |
| `QuicPunch.Vpn` | C#, P/Invoke, Wintun/TUN y broker | Adaptadores y operaciones privilegiadas de red |
| `QuicPunchTests` | Ejecutable C# con flags y pruebas Node/VM/navegador | No es una colección convencional de tests descubiertos por `dotnet test`; debe usarse su ejecutor |
| Android | `net11.0-android37.0`, C/JNI/P/Invoke y código compartido | Aplicación móvil y transporte MsQuic ARM64 |
| Geolocalización | Biblioteca `net10.0`, aplicación `net11.0`, Razor, JS y herramientas Python | Catálogos, estimación y banco de trabajo; no decide la confianza del peer |
| Scripts y distribución | PowerShell, shell y herramientas C# | Compilación, firma, publicación y limpieza |

Versiones resueltas en la clausura del proyecto de pruebas: Concentus 2.2.2, NBitcoin.Secp256k1 4.0.4, Microsoft.Data.Sqlite y System.IO.Hashing 11.0.0-rc.2.26504.105, MaxMind.Db 5.2.0, ZstdSharp.Port 0.8.8, Photino.NET 4.0.16 y Photino.Native 4.0.22. El SDK disponible fue 11.0.100-rc.2.26504.105 en Windows x64. El manifiesto nativo fija MsQuic en `80a065112426bce68c1da42d026478d3e40fd45e`, con grupos `X25519MLKEM768:X25519`.

### Flujo de establecimiento y comunicación

1. `CertManager` carga o crea identidades persistentes distintas para WAN y Tor. La identidad de peer utilizada para pinning es SHA3-256 de la clave pública; un archivo de identidad existente e ilegible provoca error, no sustitución silenciosa.
2. Tokens, anuncios LAN y eventos Nostr proporcionan candidatos. Nostr verifica firma Schnorr y la vinculación del anuncio con la identidad del certificado. Un anuncio no concede permisos de aplicación.
3. El control UDP intercambia Hello/Interrogation firmados, retos, marcas temporales y entropía de sesión. `PeerInfo` deriva material direccional mediante ECDH y HKDF-SHA256. El formato UDP antiguo de datos de aplicación está retirado.
4. STUN aprende endpoints públicos y el coordinador conserva muestras de mapeo. PCP, NAT-PMP y UPnP negocian y renuevan puertos cuando están habilitados. La búsqueda de puertos y el mecanismo birthday tienen presupuestos explícitos.
5. El camino WAN nomina un endpoint y entrega el puerto al transporte QUIC. La configuración cliente y servidor valida el hash esperado del certificado remoto; el servidor pide certificado de cliente. MsQuic transporta los streams con TLS y los datagramas nativos.
6. Un `QuicPeerMultiplexer` mantiene el transporte físico y abre sesiones virtuales de protocolos. Su canal de control usa cabeceras binarias de 44 bytes; las solicitudes incorporan identidad de sesión y protocolo. Los streams de aplicación llevan un prefacio de seis bytes.
7. La confianza local y la autorización de aplicación son capas distintas. `AuthorizeIncomingSessionAsync` y las políticas de protocolo intervienen antes del manejador. Los grants de recuperación están vinculados a peer, protocolo, sesión y propietario; no son permisos universales de reconexión.
8. Tor utiliza servicios onion, SOCKS y carriles TCP con prefacios y tokens de conexión. La interfaz compatible con QUIC permite reutilizar los protocolos, pero sus datagramas emulados viajan sobre un transporte fiable y ordenado. No ofrecen las mismas propiedades de pérdida o bloqueo que UDP nativo.
9. La recuperación mantiene intenciones de sesión, espera la limpieza del manejador anterior y aplica ventanas, reintentos y revocación. La política predeterminada limita el transporte a 64 peers, cuatro intentos concurrentes, heartbeat cada dos segundos, vencimiento de ocho segundos y recuperación de un minuto.
10. La WebUI escucha en loopback, valida Origin y exige CSRF para las operaciones protegidas. La aplicación registra chat, voz, VPN, velocidad y los dos protocolos QuicWatch. Las actualizaciones verifican firma del manifiesto y tamaño/hash del artefacto.

Referencias principales: `QuicPunch.cs`, `QuicPunch.PeerTransport.cs`, `QuicPunch.SessionRecovery.cs`, `QuicPunchConnection.cs`, `Structures/PeerInfo.cs`, `Helpers/CertManager.cs`, `Multiplexer/`, `Tor/`, `QuicNet/Program.cs` y `QuicNet.Shared/WebUi/`.

### Garantías y límites que no deben confundirse

- La autenticación de transporte no concede confianza ni permiso para voz, VPN, disco o QuicWatch. El autoenlace de peers descubiertos es una decisión explícita de arquitectura.
- El camino de control/STUN y los candidatos WAN examinados son IPv4. Encontrar tipos IPv6 en cachés, telemetría o librerías no demuestra conectividad P2P IPv6 completa. El uso de «dual-stack» en la descripción del proyecto se refiere también a WAN/Tor y no permite prometer compatibilidad universal IPv4/IPv6.
- No se encontró una implementación completa de TURN o de negociación ICE interoperable. La búsqueda propia de candidatos y el fallback Tor no deben presentarse como una certificación ICE/TURN.
- QUIC protege y controla recursos de transporte. Sus límites de streams no limitan el número de trabajos que una aplicación crea leyendo mensajes del mismo stream, como demuestra F01.
- Las comprobaciones de hash en archivos proporcionan integridad de transferencia; no hacen seguro el contenido elegido por el remitente ni sustituyen la autorización de ruta/operación.
- El fallback Tor no garantiza establecimiento inmediato ante cualquier combinación de NAT, firewall, disponibilidad de relays o disponibilidad de Tor.

## 3. Tabla de hallazgos confirmados

| ID | Problema | Gravedad | Estado de verificación | Evidencia | Ubicación |
| --- | --- | --- | --- | --- | --- |
| F01 | Solicitudes de rangos QuicWatch crean trabajo sin límite de aplicación | Alta | CONFIRMADO | 3.136 bytes de solicitudes generan 64 trabajos pendientes; mismo resultado en tres ejecuciones finales | `MediaTransferHandler.cs:139–147`, `MediaLibrary.cs:156–170` |
| F02 | Caché remota sin presupuesto ni retirada de archivos/handles | Alta | CONFIRMADO | 24 anuncios de 4 KiB crean 24 archivos y entradas; salir conserva todo; Dispose conserva los archivos | `MediaLibrary.cs:12–28,114–119,173–176`, `RoomCoordinator.cs:43–55,391–404` |
| F03 | Cierre de sesión destruye el semáforo con escritores esperando | Media | CONFIRMADO | Quedan trabajos y origen abierto tras EOF y cancelación; el control que espera antes de destruir libera 64/64 | `MediaTransferHandler.cs:43–65,123–130,225–253` |
| F04 | Invitación QuicWatch Tor comparada con identidad WAN | Media | CONFIRMADO | Emisor autenticado y destinatario Tor correctos, pero sala rechazada; con Target WAN se acepta | `QuicNet/Program.cs:135`, `WatchControlHandler.cs:63–72`, `RoomCoordinator.cs:350–352` |
| F05 | Flush del logger compite con escritura asíncrona | Media | CONFIRMADO | Un fallo con archivos reales y reproducción controlada 3/3 con `InvalidOperationException` | `RotatingFileLogger.cs:139–176` |
| F06 | Batería web falla por fixtures y expectativas desactualizadas | Baja | CONFIRMADO | 4/19 pruebas fallan al ejecutarlas; los símbolos existen en producción y faltan en sus fixtures | Cuatro archivos `*WebTests.mjs` detallados en §4.6 |

Las ubicaciones abreviadas de QuicWatch parten de `QuicNet.Shared/Watch/`; las del logger, de `QuicPunch/Helpers/`. El anexo contiene rutas, hashes, comandos y código completo del ejecutor de reproducción.

## 4. Diagnóstico y planes individuales

### 4.1 F01 Solicitudes QuicWatch sin presupuesto de trabajo

**Impacto y condiciones.** Un contacto con sesión de medios autorizada y permiso de lectura del medio actual puede enviar solicitudes válidas pequeñas mientras deja de consumir las respuestas. Aumentan los trabajos, archivos de origen abiertos, buffers y operaciones pendientes. No es una vía de acceso para un peer desconocido ni permite leer fuera del archivo seleccionado. Puede ocurrir con un cliente defectuoso, además de con un contacto malicioso.

**Causa raíz.** `ProcessFrame` crea un `Task.Run` por cada petición válida y no conserva ni limita esas tareas. `MediaLibrary.ServeRemoteRangeAsync` valida pertenencia a la sala, medio y rango, pero abre un `FileStream` por solicitud. `SendRangeAsync` alquila un buffer y prepara datos antes de esperar al escritor. `Session.WriteLock` serializa bytes de salida; no impide acumular peticiones detrás del escritor. El máximo de 1 MiB por rango y el límite de tamaño por frame no imponen un límite agregado.

**Reproducción.** Ejecutar `--audit-repros` del anexo. La prueba crea un archivo de 65.536 bytes, una sala local y un participante autorizado. Alimenta el manejador real con 64 frames Request válidos de 49 bytes y bloquea la salida del stream simulado. El callback instrumentado envuelve la función real de `MediaLibrary`; no la sustituye por una función que omita sus permisos.

Esperado: el lector deja de admitir solicitudes, aplica una cola acotada o rechaza exceso de trabajo antes de abrir recursos. Observado:

```text
RANGE pressure: requestBytes=3136; callbacksStarted=64; completed=0;
writerBlocked=True; outstandingTasks=64
```

Se observó lo mismo en las tres ejecuciones finales. La prueba simula la contrapresión del stream, no una topología Tor real. La extrapolación a agotamiento de memoria/handles se apoya en la creación sin límite y en los recursos retenidos por cada trabajo; no se provocó deliberadamente OOM ni se midió un umbral de caída.

**Corrección propuesta.** Introducir admisión por sesión antes de crear la tarea y antes de abrir el archivo. Como punto de partida revisable, permitir cuatro rangos activos por peer y una cola máxima de 16; mantener además un presupuesto global y otro en bytes. Al llenarse, esperar capacidad en un worker acotado o responder Cancel/Busy. Evitar envolver peticiones admitidas en una lista ilimitada de tareas que esperan un semáforo: eso solo desplaza el crecimiento. Registrar los trabajos admitidos, observar todas sus excepciones y vincularlos a una vida de sesión cancelable. Procesar Cancel para los trabajos salientes por `RequestId`; actualmente ese frame resuelve peticiones entrantes pendientes de respuesta, no un registro de trabajos de servicio.

Alternativas: un único worker por sesión simplifica el orden y los límites; varios workers acotados permiten más paralelismo. Limitar solo por segundo no resuelve a un receptor bloqueado durante mucho tiempo. El presupuesto global debe repartir capacidad para que un peer no impida servir a los demás.

**Regresión y compatibilidad.** Si se añade Busy al protocolo, negociar versión o utilizar un rechazo ya entendido por versiones anteriores. Puede reducirse throughput con límites demasiado pequeños; medir reproducción y búsquedas de vídeo en varios peers. Implementar junto con F03 para que la admisión y la limpieza tengan el mismo propietario.

**Validación de salida.** Con 1.000 solicitudes y un receptor bloqueado, trabajos activos, cola, handles y bytes deben permanecer bajo límites documentados. La petición 21 no debe crear un nuevo trabajo si la política es 4+16. Tras cancelar o cerrar, todos los trabajos deben terminar y el origen debe poder abrirse con `FileShare.None` en menos de cinco segundos. Comprobar rangos distintos, duplicados, Cancel, peer desconectado, permisos revocados y servicio simultáneo a otro peer.

### 4.2 F02 Caché remota acumulativa y archivos preextendidos

**Impacto y condiciones.** Un propietario de sala cuyo protocolo de control ya fue autorizado puede anunciar repetidamente identificadores de medios nuevos. El receptor crea cachés aunque no haya solicitado reproducir esos datos. Los medios anteriores mantienen archivos y handles durante la vida de `MediaLibrary`; los archivos permanecen después de Dispose y no se encontró un presupuesto o barrido de esa carpeta en la ruta examinada. Un uso normal con muchos vídeos también acumula recursos.

**Causa raíz.** `RegisterRemoteMedia` valida tamaño positivo y chunk, pero no limita tamaño de medio, número de cachés ni espacio total. El constructor abre el archivo y llama a `SetLength(manifest.Size)`. `RoomCoordinator.UpdateRemoteMedia` registra cada nuevo `MediaId`; cambiar de medio o abandonar la sala no retira las entradas anteriores. `MediaLibrary.Dispose` cierra entradas y borra el diccionario, pero no elimina los archivos. El código de limpieza general del repositorio se dirige a resultados de desarrollo y no es un mecanismo de cuotas para la caché de usuario.

**Reproducción.** `Cache` en `--audit-repros` entrega al `RoomCoordinator` real una invitación dirigida al receptor y 23 mensajes media sucesivos del mismo propietario, cada uno con un `MediaId` nuevo y tamaño 4.096. No descarga contenido ni usa tamaños peligrosos.

```text
CACHE before leave: files=24; bytes=98304
CACHE after leave: roomIsNull=True; files=24; entries=24
CACHE after dispose: files=24; bytes=98304
```

Esperado: una política explícita de retención y límites retira entradas o impide admisión fuera del presupuesto. Observado: todos los anuncios crean caché y ninguna de estas transiciones la retira. No se afirma que 98.304 bytes por sí solos constituyan DoS ni que la longitud lógica equivalga siempre a bloques físicos asignados. El fallo demostrado es crecimiento y retención sin presupuesto; su escala depende de tamaños, sistema de archivos y duración de uso.

**Corrección propuesta.** Definir máximo de medio, presupuesto global de caché, presupuesto por origen y máximo de handles abiertos. Validarlos antes de crear/abrir/preextender el archivo. Crear la caché al primer rango solicitado o utilizar almacenamiento de chunks; evitar reservar por anuncio todo el tamaño remoto. Incorporar retención LRU/TTL y liberar el medio anterior al dejar de utilizarlo. `LeaveAsync`, `room_closed`, cambio de propietario y cierre deben notificar al propietario de la caché para retirar referencias.

Si se desea persistencia entre sesiones, guardar un índice limitado de rangos realmente verificados, cerrar handles inactivos y limpiar archivos huérfanos al inicio. Si no se desea persistencia, eliminar los archivos al expulsar y al cerrar. Usar referencias activas para no borrar un archivo durante una respuesta HTTP. El constructor debe cerrar el stream y retirar el archivo incompleto si `SetLength` u otra inicialización falla.

**Alternativas y regresión.** Una caché temporal de la sala actual es la solución más sencilla; una caché persistente aporta reutilización, pero exige metadatos y cuotas durables. El límite no debe impedir vídeos legítimos sin una indicación comprensible al usuario. No basar la defensa exclusivamente en espacio libre porque múltiples peticiones concurrentes pueden reservarlo simultáneamente.

**Validación de salida.** Repetir 1.000 cambios pequeños de medio; archivos, handles y tamaño deben estabilizarse en la cuota. Anunciar un tamaño por encima del máximo debe rechazarse antes de `SetLength`. Verificar cambio de medio durante lectura, salida de sala, reconexión, reinicio, carpeta llena, fallo de permisos y fallo de inicialización. Confirmar retirada al inicio de archivos huérfanos sin borrar contenido ajeno a la caché.

### 4.3 F03 Destrucción de la puerta de escritura con tareas pendientes

**Impacto y condiciones.** Si termina el lector de una sesión mientras varias respuestas esperan la puerta de escritura, el cierre puede dejar tareas que no concluyen ni después de cancelar su token. Esas tareas retienen el archivo origen abierto y otros recursos. No requiere una gran carga: la concurrencia de respuestas admitida por el protocolo es suficiente.

**Causa raíz.** El `finally` de `MediaTransferHandler.HandleAsync` ejecuta `session.Dispose()` sin cancelar y esperar los trabajos que creó. `Session.Dispose` destruye `WriteLock`, aunque esos trabajos estén en `WaitAsync` o vayan a ejecutar `Release`. Dispose no es un mecanismo de cancelación de esperadores. Microsoft exige que las demás operaciones del semáforo hayan terminado antes de destruirlo. Véase [SemaphoreSlim.Dispose](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim.dispose?view=net-10.0).

**Reproducción y refutación.** La prueba de F01 tiene dos órdenes de cierre, sin modificar el código bajo prueba:

1. EOF del stream, esperar que termine `HandleAsync`, cancelar la vida y esperar los trabajos tres segundos.
2. Como control, mantener abierto el lector, cancelar y esperar todos los trabajos, y después entregar EOF.

En las tres ejecuciones finales del primer orden quedaron respectivamente **4, 15 y 3 tareas** pendientes y `sourceReleased=False`; el `WhenAll` agotó su plazo. En los tres controles terminaron **64/64**, quedaron cero tareas y `sourceReleased=True`. Las repeticiones anteriores también reprodujeron el problema, con otra cantidad de esperadores. El número exacto depende de la planificación; la diferencia observable entre ambos órdenes es consistente.

Este ensayo utiliza un stream controlado para establecer el orden. No se presenta el error posterior de limpieza de un directorio del primer prototipo del ejecutor como caída del producto: ese error fue la consecuencia de intentar borrar un origen todavía abierto dentro del diagnóstico. El ejecutor final difiere la eliminación hasta que sale su proceso.

**Corrección propuesta.** Cada Session debe poseer un CTS y el registro de sus trabajos. El cierre debe: impedir admisiones; cancelar el CTS; abortar/disponer el stream para desbloquear IO; esperar los trabajos admitidos observando sus excepciones; y solo entonces destruir `WriteLock`. El cierre necesita ser idempotente y no esperarse a sí mismo desde uno de los trabajos. No mantener un lock de estado mientras se espera la finalización.

Como mitigación mínima, no destruir una puerta administrada todavía utilizada; esto evita el uso inválido del semáforo, pero no sustituye la cancelación y espera de los trabajos. Revisar con el mismo criterio `WatchControlHandler.Session`, `MediaLibrary.RemoteCache` y `WebUiWebSocketHub.ClientConnection`; comparten patrones de destrucción, pero sus efectos específicos no se contabilizan como nuevos defectos confirmados en esta auditoría.

**Regresión y validación.** No cerrar el transporte físico compartido si solo termina una sesión virtual. Evitar que un reemplazo de sesión haga que respuestas antiguas se escriban en la nueva. Repetir el ensayo con EOF, excepción de lectura, cancelación, revocación y reemplazo; todos los trabajos y archivos deben liberarse. Añadir una comprobación que falle si `Release` usa un semáforo destruido. Un cierre satisfactorio debe permitir acceso exclusivo al archivo origen y no dejar excepciones sin observar.

### 4.4 F04 Incompatibilidad entre la identidad local WAN y Tor en QuicWatch

**Impacto y condiciones.** La aplicación de escritorio construye `RoomCoordinator` con `qcc.CurrentPeer.Id`, correspondiente a WAN. Las identidades Tor son distintas por diseño. Una invitación dirigida al ID Tor del receptor es descartada aunque el emisor y destinatario sean correctos para el transporte autenticado. No se ha confirmado una fuga de la identidad WAN; el problema demostrado es funcional.

**Flujo exacto.** `QuicNet/Program.cs` fija un único `_localPeerId`. `WatchControlHandler.ForPeer` transforma el emisor al ID de la identidad Tor local al enviar sobre Tor y conserva el destinatario remoto Tor. El receptor valida `SenderPeerId == peer.Id`, que pasa. Después, `AcceptRoomState` compara `TargetPeerId != _localPeerId` y retorna porque `_localPeerId` es WAN.

**Reproducción.** La prueba construye identidades WAN/Tor diferentes, ejecuta la transformación real `ForPeer` y entrega el mensaje resultante al manejador real `HandleMessageAsync`:

```text
TOR invite: authenticatedSenderMatches=True;
targetMatchesLocalTor=True; accepted=False
TOR control with WAN target: accepted=True
```

La segunda variante solo demuestra qué comparación provoca el rechazo; **enviar el ID WAN por Tor no es la corrección propuesta**. No se necesitó iniciar Tor público: la reproducción verifica determinísticamente los métodos y los IDs usados por la aplicación.

**Corrección propuesta.** Sustituir la suposición de un único ID local por una resolución de identidad asociada al transporte autenticado de la sesión. Pasar al coordinador un proveedor de identidad local por peer/transporte y registrar en el estado de sala el ámbito local elegido. Comprobar que la invitación apunta a la identidad local correspondiente a ese canal. Aplicar la misma resolución a participantes, propiedad, control, permisos, selección de medios y salida de sala.

Alternativa: coordinadores separados por identidad/transporte, con la WebUI seleccionando explícitamente el ámbito. Si se permiten salas mixtas WAN/Tor, definir la traducción local de participantes sin publicar asociaciones entre identidades. Aceptar cualquier ID recibido o eliminar la comprobación del destinatario debilitaría el contrato; no hacerlo.

**Regresión y validación.** Añadir matriz WAN→WAN, Tor→Tor, invitación equivocada, sender equivocado, salida, nueva invitación y recepción tras reconexión. Tor→Tor debe aceptar el ID Tor correcto y seguir rechazando el ID WAN en ese canal. Los frames que circulen por Tor no deben contener IDs ni otros datos de la identidad WAN. Completar después una prueba E2E con dos servicios onion de laboratorio.

### 4.5 F05 Cola vacía no significa escritor de registros inactivo

**Impacto y condiciones.** Un consumidor de `RotatingFileLogger.FlushAsync`, o del wrapper global correspondiente, puede recibir una excepción mientras el worker está escribiendo. La ruta examinada no contiene una llamada de la aplicación de escritorio a Flush que permita afirmar que esto derriba la aplicación normal; el defecto está en la API pública y en la fiabilidad de su contrato. Tampoco se atribuye a esta carrera toda pérdida de logs.

**Causa raíz.** `FlushAsync` espera a que el Count del canal sea cero y después llama a `_currentWriter.Flush()` dentro de `_lock`. El worker ya puede haber retirado el último mensaje de la cola y estar en `await WriteLineAsync` o `FlushAsync`, fuera de ese lock. La exclusión no protege las dos operaciones y `StreamWriter` rechaza IO simultáneo. El bucle de espera de la cola tiene además un límite temporal, por lo que no representa una barrera de procesamiento.

**Evidencia real.** Una ejecución del test existente `--test-rotating-logger` falló en su primer caso con:

```text
System.InvalidOperationException: The stream is currently in use
by a previous operation on the stream.
at QuicPunch.Helpers.RotatingFileLogger.FlushAsync() ... line 152
```

Ocho repeticiones posteriores del mismo test pasaron. Esto refuta una explicación de fallo constante de formato o de compresión, y muestra que la prueba normal depende de la planificación.

**Reproducción determinista.** `Logger` en el anexo sustituye únicamente el sink de salida, por reflexión en el proceso de diagnóstico, por un stream que retrasa la finalización de una escritura. El worker y `FlushAsync` son los reales. Se encola una línea de 8.192 caracteres, se espera a la escritura pendiente y se pide Flush cuando ya no quedan mensajes en cola. La misma excepción se produjo **3/3** veces. Al liberar el sink el worker puede finalizar normalmente.

**Corrección propuesta.** Dar al consumidor del canal la propiedad exclusiva del `StreamWriter`. Representar Flush como una orden/barrera con `TaskCompletionSource` que el consumidor ejecute después de los mensajes anteriores. Rotación y cambio de directorio deben entrar también por ese propietario, en vez de manipular el escritor desde otro hilo. Garantizar que una política de descarte de logs no descarte silenciosamente órdenes Flush: usar una vía de control no descartable o un canal de comandos con admisión explícita.

Alternativa: serializar todas las operaciones con una puerta asíncrona y llevar secuencias para esperar el procesamiento de los mensajes anteriores. Un lock alrededor del Flush actual, sin que el worker participe ni una barrera de secuencia, no resuelve el problema. No aplicar una espera fija o reintentar ignorando `InvalidOperationException` como solución.

**Regresión y validación.** Especificar si Flush garantiza escritura al stream o también persistencia física; no prometer fsync si no se implementa. Con IO retrasado, Flush debe esperar y después completar sin concurrencia ni pérdida de los mensajes admitidos anteriores. Probar múltiples Flush, cola llena, rotación, cambio de directorio, fallo de disco y cierre simultáneo. Mantener acotada la cola y medir el coste del nuevo canal de control.

### 4.6 F06 Cuatro pruebas web no alcanzan sus comprobaciones completas

**Impacto.** `scripts/test.ps1 -Web -NoBuild` devuelve 1: pasan 15 de las 19 pruebas enumeradas y fallan cuatro. Esto limita la utilidad de la batería como barrera de regresión. No demuestra por sí solo cuatro errores de la interfaz de producción.

| Prueba | Fallo observado | Contraste con implementación |
| --- | --- | --- |
| `ChatConnectionWebTests.mjs` | `QP.peerInConnectionMode is not a function` | El fixture QP no incluye el helper que usa `chatTrustedPeers`; `app.js` lo define y exporta |
| `PeerConnectionStateWebTests.mjs` | El mismo TypeError desde `dashboard.js` | Mismo contrato QP incompleto en el fixture |
| `ChatNavigationWebTests.mjs` | `closeEmojiPopover is not defined` | Ejecuta solo el prefijo de `chat.js`; deja fuera ese helper y otras dependencias que el selector utiliza |
| `ProfileAvatarWebTests.mjs:9` | `524288 !== 327680` | Frontend, worker y `ProfileAvatarStore` usan 512 KiB; el test y la documentación aún dicen 320 KiB |

**Reproducción exacta.** Desde la raíz, ejecutar `& scripts/test.ps1 -Web -NoBuild`. Para aislar, ejecutar `node QuicPunchTests/Tests/<nombre>.mjs`. La política de avatar está alineada entre las tres implementaciones observadas: el desajuste demostrado está en test/documentación, no entre cliente y servidor.

**Corrección propuesta.** Actualizar los fixtures QP para que implementen el contrato de filtrado por modo y añadir casos WAN/Tor, en vez de devolver siempre true para hacer desaparecer el error. Cargar las funciones y estado necesarios para el selector de conversación o extraer su lógica comprobable a un módulo compartido; evitar recortar código por una coincidencia textual que deja fuera dependencias. Ajustar la comprobación de avatar al presupuesto decidido: 512 KiB coincide con el producto actual; si el requisito sigue siendo 320 KiB, debe tratarse como una decisión de producto y cambiar coherentemente las implementaciones. Actualizar los mensajes y la documentación.

**Validación.** Las 19 pruebas deben pasar sin omitir aserciones ni capturar esos errores. Comprobar borradores por contacto, aislamiento del chat público, filtrado de conexiones WAN/Tor, estado QUIC y límites exactos de avatar, incluido límite+1. Mantener una prueba de integración que cargue los módulos reales para detectar desajustes de fixtures. El primer fallo de cada prueba puede ocultar otros; después de corregirlo hay que ejecutar el archivo completo.

## 5. Hallazgos no confirmados y sospechas descartadas

| ID | Observación | Clasificación | Qué falta o por qué se descarta |
| --- | --- | --- | --- |
| N01 | `SimpleStunClient.TryProcessIncoming` extrae la transacción antes de validar el cuerpo y no compara el endpoint emisor con `PendingRequest.Remote` | HIPÓTESIS de impacto de seguridad | El ID es aleatorio de 96 bits y el canal STUN ya es una fuente de candidatos no autenticada. Falta una reproducción de contaminación o interrupción con capacidad de atacante delimitada y consecuencias pese a los controles posteriores. No se declara bypass de identidad |
| N02 | Destrucción de semáforos en otras clases de WebSocket/control/caché | PROBABLE, NO CONFIRMADO para esas rutas | Comparte el patrón de F03, pero faltan interleavings reproducidos y alcance real por clase. Investigar con cierre remoto y escrituras pendientes; no sumar incidencias solo por ver Dispose |
| D01 | Conexión automática a descubierto podría conceder acceso de aplicación | DESCARTADO en los casos probados | La sesión de transporte y la confianza/permisos son distintos. Pasan pruebas de autorización, revocación y transporte automático |
| D02 | Callback TLS que no valida CA pública permite aceptar cualquier certificado | DESCARTADO para el flujo observado | El esquema usa pinning explícito del hash de la clave del certificado esperado, tanto en cliente como en servidor |
| D03 | Identidad dañada sería reemplazada silenciosamente | DESCARTADO para bundles/legacy examinados | `CertManager` falla y pide restaurar identidad; pasan pruebas de persistencia y fiabilidad crítica |
| D04 | Reserva virtual cancelada pierde capacidad permanentemente | DESCARTADO en los escenarios de la suite crítica | Pasan las comprobaciones de rechazo, cancelación, revocación y fallo de lookup |
| D05 | Un frame que cabe en un datagrama no dispara OnFrameReceived | DESCARTADO como incumplimiento demostrado del contrato actual | La documentación distingue recepción categorizada y reensamblado multidatagrama. Haría falta definir otro contrato para exigir el mismo callback; no es prueba suficiente de pérdida de vídeo de la aplicación actual |
| D06 | Resultado vacío del catálogo STUN bajo el aislamiento demuestra fallo de NAT | DESCARTADO como conclusión sobre el producto | Se sustituyeron catálogos/hosts externos por loopback y se bloquearon HTTP no locales; la expectativa externa del test deja de cumplirse |

Para N01, la siguiente investigación debe usar dos servidores STUN locales y un tercero que reciba el ID solo dentro del laboratorio; separar paquetes con fuente equivocada, cuerpo truncado y respuesta válida posterior, y observar si cambia el mapeo o solo se pierde una muestra. El modelo de [STUN en RFC 8489](https://www.rfc-editor.org/rfc/rfc8489.html) ayuda a delimitar esa prueba; no sustituye demostrar explotabilidad en QuicPunch. No se propone aquí una corrección definitiva de una vulnerabilidad no demostrada.

## 6. Pruebas realizadas y evidencia observable

### Compilación y dependencias

- `dotnet build QuicPunchTests/QuicPunchTests.csproj -c Test --nologo -v quiet`: correcto, cero errores y seis avisos CS8602 en `SecurityDiscoveryTests.cs`. El primer build es la referencia de ese recuento; builds incrementales posteriores pueden no repetir avisos.
- `dotnet build QuicPunch.slnx -c Debug --no-restore`: no completado. Faltaba el workload Android (`NETSDK1147`) y había DLL Debug bloqueadas por aplicaciones/Visual Studio que ya estaban abiertas. No se cerraron procesos del usuario ni se instalaron workloads. Son restricciones del entorno, no defectos de compilación confirmados del producto.
- Se intentó primero la configuración Test para la solución: no existe en la solución y se descartó ese comando como comprobación válida; Test sí se utiliza para compilar el proyecto de pruebas.
- `dotnet list ... package --vulnerable --include-transitive --format json`: salida 0 sin paquetes vulnerables enumerados por las fuentes configuradas. Esto no cubre binarios MsQuic/OpenSSL/Tor/Wintun, WASM, herramientas descargadas ni vulnerabilidades que aún no figuren en esas fuentes.

### Suites .NET

**32 suites aprobaron**, una ejecución adicional de catálogo STUN quedó incompleta por el aislamiento y el logger mostró el fallo intermitente de F05. No se agregan los casos internos como si fueran ejecuciones independientes de suites.

Suites aprobadas: `sha3`, `antireplay`, `punch-packet`, `peer-acl`, `session-recovery`, `resumable-files`, `critical-reliability`, `connectivity-security`, `connection-recovery`, `peer-state`, `vpn-policy`, `vpn-orchestration`, `vpn-broker`, `virtual-disk`, `updates`, `webui-security`, `nostr-mailbox`, `tor-privacy`, `pqc`, `quic-datagrams`, `quic-optimizations`, `lan-pooling`, `lan-ports`, `opus`, `automatic-transport`, `discovery-announcements`, `discovery-presence`, `contact-history`, `geolocation`, `token-readiness`, `platform-video` y `notifications`.

Además se invocaron individualmente **18 métodos de SecurityDiscoveryTests**, todos satisfactorios, seleccionados para no arrancar gateways o Tor real: identidad/persistencia, filtrado de IP, roles y tiempo, introspección/batching, replay de Hello y derivación de contraseña, confianza, token/firma Nostr, límite del rate limiter, poda/admisión, framing de candidatos, TTL/capacidad de handshakes, caché de endpoints, tokens Tor, identidad Nostr, estabilidad de token, leaveOpen del transporte dummy, nombres/saneamiento de certificados y aislamiento de peers compartidos.

La prueba `nat-pin-coordinator` llegó a comprobar anuncios de puertos, LAN real local y buffers/consenso de NAT, pero falló al exigir una colección inicial no vacía de endpoints STUN externos. Se registra como prueba parcial, no como suite aprobada ni como hallazgo funcional. El logger falló una vez con archivos reales; ocho repeticiones ordinarias pasaron; el ensayo controlado confirmó la carrera tres veces.

### Pruebas web, fuzzing y red adversa simulada

- Node v24.14.1: 19 archivos `*WebTests.mjs`, 15 aprobados y cuatro fallidos descritos en F06. Son pruebas de módulos/VM y estructura; no equivalen a 19 recorridos completos de navegador.
- Fuzzing con semilla 8489: 30.000 entradas de datagramas de hasta 512 bytes, combinando datos raw, marcador forzado y longitudes cortas; también se ejercitó el reensamblador. 29.065 fueron parseadas y el resto rechazadas. El formato admite datagramas raw, por lo que aceptar una entrada aleatoria no implica una anomalía.
- Se decodificaron 2.000 tokens Base64URL aleatorios de hasta 256 bytes: cuatro produjeron una estructura y el resto fueron rechazados con excepciones de formato/datos esperadas. No hubo excepciones inesperadas. No se interpretan las estructuras aceptadas como identidades autenticadas ni se conectó a ellas.
- Benchmark QUIC real sobre un proxy UDP **local** con 40 ms de retraso por sentido y descarte determinista de un paquete de cada 500. Resultado de una ejecución: subida 416,81 Mbit/s, bajada 409,15 Mbit/s, latencia basal 91,61 ms y bajo carga 113,44 ms. El proxy contó 385.010 paquetes de subida/770 descartes y 370.813 de bajada/741 descartes. Duró aproximadamente 13,1 s. Es una comprobación de operación bajo retraso/pérdida; no mide capacidad de NIC, WAN real ni distribución estadística de rendimiento.
- Las tres ejecuciones finales de las reproducciones específicas terminaron con salida 0 y reprodujeron F01–F05. El ejecutor constata comportamientos defectuosos; salida 0 significa que el diagnóstico terminó, no que el producto haya pasado esos casos.

### Aislamiento

El wrapper temporal activa la fixture existente `LoopbackOnlyNetwork`: STUN/Nostr/catálogos se redirigen a loopback, se evita sincronización de reloj externa y HTTP no local utiliza un proxy loopback que no presta servicio. No es una sandbox de red del sistema operativo. Algunas suites usan sockets en interfaces locales y anuncios sintéticos de candidatos WAN; sus objetivos de prueba son procesos/fixtures del laboratorio. No se realizaron pruebas intrusivas contra terceros, instalaciones de broker ni cambios deliberados de rutas o mapeos del router.

Las reproducciones nuevas no capturan audio ni pantalla, no arrancan servicios Tor públicos y limitan la carga a 64 solicitudes y cachés de 4 KiB. El benchmark usa exclusivamente loopback. Las claves de identidad generadas por tests son de fixture; no se consultó ni reemplazó la clave privada de publicación.

La limpieza final recursiva de los directorios de diagnóstico `qp-p2p-audit-*` en Temp fue rechazada automáticamente por política. Esos temporales no se borraron; sus rutas concretas constan en la evidencia. Esto no impidió conservar los resultados ni afecta a las fuentes del producto.

## 7. Cobertura y límites

| Área solicitada | Cobertura conseguida | Límite pendiente |
| --- | --- | --- |
| Estructura, lenguajes, versiones y arquitectura | Inventario, proyectos, dependencias, documentación y flujos principales | El árbol estuvo cambiando; no es una lectura línea a línea de todos los proyectos auxiliares |
| Descubrimiento y confianza | Hello, tokens, Nostr, LAN, permisos; suites y métodos seleccionados | Sin pruebas intrusivas sobre relays públicos; N01 pendiente |
| Hole punching, NAT y puertos | Inspección de candidatos, STUN, mapping/renovación, búsqueda/birthday; pruebas locales de anuncios y LAN | Sin matriz física con NAT simétrico, doble CGNAT, móvil y dos routers; catálogo externo parcial |
| QUIC, MTU y datagramas | Pinning, PQC, datagramas, configuración nativa y transferencia con checksum; benchmark con pérdida/retraso | Sin matriz completa de PMTU de WAN ni jitter/reordenamiento controlado por separado |
| Concurrencia y sesiones | Recuperación, glare en escenarios existentes, límites, revocación; repro de cierre y contrapresión | Sin detector exhaustivo de carreras ni prueba prolongada de 64 peers reales |
| Seguridad de aplicación | ACL, CSRF/Origin, firmas/hash de updates, rutas/integridad de disco y archivos; F01–F04 | Sin pentest completo, análisis criptográfico formal ni fuzzing de todos los protocolos |
| Recuperación y persistencia | Identidad, contactos, ficheros parciales, reconexión y fallos de adaptador simulados | Sin corte físico de alimentación, fallos del kernel ni simulación completa de corrupción de disco |
| Rendimiento y recursos | Benchmark local y crecimiento bajo cargas pequeñas controladas | Sin perfil completo de CPU/heap/handles durante horas ni ensayo de agotamiento del equipo |
| Voz y vídeo | Opus, plataforma sintética, tests JS de audio/vídeo/colas | Sin captura real, latencia perceptual, múltiples dispositivos ni prueba de llamada larga |
| VPN | Políticas, broker, orquestación y adaptadores simulados; inspección de ruta nativa sin modificarla | Sin instalar servicio privilegiado ni validar encaminamiento real en Windows/Linux durante esta auditoría |
| Tor | Inspección de carriles/runtime, pruebas de privacidad y reproducción determinista de IDs | Sin dos circuitos onion reales ni atribuir propiedades de QUIC nativo a datagramas emulados |
| Geolocalización | Flujos de caché, privacidad y suite con catálogos/HTTP simulados | Sin validar precisión geográfica, algoritmos científicos completos ni todas las herramientas auxiliares |
| Distribución y portabilidad | Proyectos, manifiesto/parches nativos, firma/hash de updates y consulta de advisories NuGet | Sin recompilar MsQuic/OpenSSL desde cero, APK/AOT, Linux/ARM64 o inventario CVE completo de binarios |
| WebUI y calidad de pruebas | Seguridad HTTP y 19 pruebas web; F06 y desajustes documentales | Sin batería completa de navegador con WebView2/Android WebView ni validación visual exhaustiva |

Documentación contrastada: hub/README, arquitectura, seguridad, suite de pruebas, estabilidad de conexiones y perfiles. El README todavía menciona cambio dinámico BBR en conexiones activas, mientras las pruebas actuales exigen preparar configuración y reconectar para cambiarlo; se trata como documentación desactualizada, no como un defecto nativo demostrado. La política de avatar de 320 KiB frente a 512 KiB está incluida en F06. La equivalencia «Tor datagram = QUIC datagram» tampoco debe inferirse de la interfaz común.

## 8. Hoja de ruta

| Orden | Trabajo | Dependencias | Esfuerzo estimado | Criterio para cerrar |
| --- | --- | --- | --- | --- |
| 1 | F01 y F03: propietario de sesión, admisión, registro, cancelación y espera de trabajos | Diseñar juntas capacidad y limpieza | Medio | Límites efectivos con receptor lento y cero trabajos/handles tras cierre |
| 2 | F02: presupuestos y ciclo de vida de cachés | Integración con cambio/salida de sala; puede desarrollarse en paralelo lógico con 1 | Medio | Archivos/handles/bytes acotados y limpieza de huérfanos comprobada |
| 3 | F04: identidad local por transporte y sala | Definir si se permiten salas mixtas | Medio | Matriz WAN/Tor pasa sin exponer identidad WAN por Tor |
| 4 | F05: escritor único y barrera Flush | Independiente de QuicWatch | Bajo a medio | Ensayo de IO retrasado y cierre/rotación simultáneos pasan |
| 5 | F06 y documentación | Política de avatar decidida | Bajo | 19/19 pruebas web completas y documentación alineada |
| 6 | N01/N02 y pruebas de despliegue pendientes | Entorno estable y laboratorio de red/plataformas | Variable | Resultados nuevos clasificados por evidencia, sin convertir sospechas en vulnerabilidades por anticipado |

Estas estimaciones expresan complejidad relativa, no compromisos de plazo. No se requiere migrar la identidad criptográfica, cambiar la clave de publicación ni sustituir el transporte para resolver los defectos confirmados.

## 9. Veredicto técnico

La base de transporte supera una batería local amplia de autenticación, recuperación, QUIC y protocolos. Los problemas comprobados se encuentran en presupuestos y cierre de recursos de QuicWatch, su selección de identidad Tor, la sincronización del logger y el mantenimiento de pruebas web.

**F01 y F02 deben bloquear una liberación del producto completo que habilite QuicWatch para contactos no plenamente fiables. F03 y F04 deben resolverse antes de dar esa función por robusta y compatible con Tor.** Deshabilitar o restringir QuicWatch reduce esa exposición, pero no corrige los defectos. Para el resto del sistema, la evidencia permite hablar de validación local satisfactoria en los casos ejecutados, no de ausencia de errores ni de preparación universal para producción.

El trabajo de esta auditoría queda entregado como análisis y planes. Ninguna propuesta de corrección se ha implementado.

## 10. Anexos

- [Reproducción completa](reproduccion.md): proyecto temporal, código de diagnóstico, runner, comandos y procedimiento de regeneración.
- [Evidencia conservada](evidencia.md): resultados observados, manifiesto de hashes, cambios concurrentes y extractos de los logs relevantes.

Los logs completos y las compilaciones temporales están en `.artifacts/p2p-audit-2026-10-22/` y pueden desaparecer con la limpieza estándar. Los anexos conservan la información necesaria para regenerar el diagnóstico sin depender de esos temporales.
