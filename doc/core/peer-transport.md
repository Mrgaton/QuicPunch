# Transporte automático de peers

QuicPunch y QuicNet preparan automáticamente una conexión QUIC para los peers descubiertos dentro del ámbito de descubrimiento del nodo: LAN, pool Nostr habilitado o conexión de señalización iniciada por el usuario. Una identidad provisional de buzón o un anuncio sin certificado no basta: primero se completa Hello y se comprueba la identidad. La negociación QUIC valida el certificado esperado y utiliza el endpoint autenticado de señalización.

Estar conectado no concede confianza, no guarda al peer ni abre aplicaciones. El canal de control gestiona sesiones, latencia y cierre por inactividad. Los streams y datagramas de aplicaciones requieren una sesión autorizada. Incluso el acceso directo al multiplexer exige confianza para abrir una sesión virtual.

La confianza habilita chat directo según la política de la aplicación. Voz, VPN, archivos y los demás protocolos conservan sus permisos o petición local. Una aprobación de transporte nunca sustituye esta decisión. Al retirar confianza se cierran las sesiones de aplicaciones y se conserva el transporte hasta que quede inactivo.

## Configuración de la librería

```csharp
var node = new QuicPunchBuilder()
    .UsePool("mi-sala")
    .WithWanNostrDiscovery()
    .WithPeerTransport(new PeerTransportOptions
    {
        AutoConnectDiscoveredPeers = true,
        MaxConnectedPeers = 64,
        MaxConcurrentAttempts = 4,
        AttemptTimeout = TimeSpan.FromSeconds(30),
        IdleTimeout = TimeSpan.FromMinutes(5),
        InitialRetryDelay = TimeSpan.FromSeconds(5),
        MaxRetryDelay = TimeSpan.FromMinutes(1)
    })
    .Build();
await node.StartAsync();
```

Estos son los valores predeterminados. `node.PeerTransport` permite cambiar la política mediante un nuevo objeto de opciones. Desactivar `AutoConnectDiscoveredPeers` deja los desconocidos bajo demanda; los contactos guardados siguen su propia política de actividad reciente y `AutoConnect`. Desactivar `AutoConnect` en un contacto prevalece sobre el descubrimiento global. La aceptación de un transporte autenticado sigue siendo independiente de la confianza.

## Contactos recientes y reintentos UDP

Al arrancar se toma como referencia la hora UTC de inicio. Un contacto con `AutoConnect` y actividad autenticada en las 48 horas anteriores es elegible durante toda esa ejecución; no caduca a mitad de una sesión larga. Los contactos sin historial o más antiguos no reciben sondeos persistentes a sus direcciones guardadas. Un nuevo anuncio Nostr cuyo token y autor hayan superado la validación reactiva al contacto antiguo durante esa ejecución, sin inventar una conexión satisfactoria ni saltarse `AutoConnect = false`. Un Hello autenticado también aporta actividad reciente.

El primer sondeo se envía inmediatamente y los intervalos siguientes son **0,5 / 1 / 2 / 4 / 8 / 16 / 25 / 25... segundos**. Estos sondeos de contactos no tienen el límite de 45 segundos de una interrogación manual; continúan mientras estén desconectados y la política siga habilitada. Un Hello válido detiene la interrogación y el transporte pasa a usar su mantenimiento habitual. Si se pierde disponibilidad se vuelve a sondear. Cambiar las direcciones mediante un anuncio validado reinicia el sondeo con el nuevo destino. Parar WAN, desactivar el contacto o detener el nodo cancela los sondeos pendientes; reiniciar el nodo borra las reactivaciones que sólo procedían de anuncios.

`LastAuthenticatedActivityUtc` se conserva en el almacén, se actualiza por Hello autenticado o respuestas correlacionadas del heartbeat y se escribe como máximo una vez por minuto. Al establecer el túnel se actualizan también esa actividad y `LastConnectedUtc`. Los almacenes anteriores usan `LastConnectedUtc` como referencia inicial. Anuncios, pings UDP sin autenticación y mensajes salientes al buzón no fabrican actividad autenticada.

Esta temporización se refiere a señalización UDP; los reintentos QUIC de 5 a 60 segundos y la ráfaga de perforación del socket QUIC mantienen su función independiente. Los 25 segundos no garantizan conservar el estado de todos los NAT.

El límite de peers cuenta conexiones establecidas y preparaciones, incluyendo solicitudes entrantes. Las preparaciones simultáneas de ambos extremos comparten el mismo espacio por identidad y mantienen la resolución de colisiones del handshake. El límite de intentos concurrentes se aplica al planificador automático local. `EnsureMultiplexerConnectedAsync` permite iniciar o reutilizar explícitamente un transporte, sujeto al límite de peers; no concede confianza.

Los fallos automáticos se reintentan con espera exponencial de 5 a 60 segundos. Cada intento tiene un plazo y se cancela al detener el nodo. Stop/restart limpia tareas y reservas de preparación; los trabajadores de una generación anterior no pueden reservar espacio en una nueva.

Un túnel sin sesiones de aplicación se cierra después del plazo de inactividad. Los mensajes periódicos de Hello y los heartbeats no cuentan como uso de aplicaciones. El mensaje de control `TransportIdle` avisa al otro extremo, de modo que ambos aparcan el túnel incluso si tienen plazos distintos. Se vuelve a preparar ante una conexión explícita, un nuevo Hello de sesión tras reiniciar el peer o una nueva incorporación tras salir de disponibilidad; no se abre de nuevo en cada ronda del planificador. Una sesión de aplicación activa o pendiente de decisión impide ese cierre.

QuicNet muestra el estado QUIC y la confianza por separado. Las decisiones de confianza y permisos siguen en el dashboard; el transporte ya se prepara antes de concederlas.

## Verificación

`--test-automatic-transport` establece QUIC nativo entre dos nodos sin confianza y comprueba bloqueo local/remoto de protocolos, aprobación separada de voz, transferencia autorizada sin otra negociación, retirada de confianza, conservación de sesiones activas, cierre/aparcamiento en ambos extremos, límites, backoff y limpieza tras restart. Las suites de recuperación, multiplexer, seguridad y cancelación cubren las rutas existentes.

`--test-contact-reconnect --loopback-only` mide paquetes UDP reales con esperas exponenciales, verifica envíos después del antiguo límite de 45 segundos, el máximo de 25 segundos, el filtro de 48 horas, anuncios inválidos y reapariciones firmadas, exclusión de contactos desactivados, cancelación y restart. La prueba tarda aproximadamente un minuto. `--test-contact-history` cubre la persistencia y el límite de escrituras de actividad.

Las pruebas locales verifican el flujo y el transporte nativo. La conectividad entre NAT distintos necesita una nueva prueba entre ambos equipos con esta compilación.

## Recuperación de sesiones de cualquier protocolo

El multiplexor comprueba la conectividad para todas las sesiones mediante pings correlacionados del canal de control. Los valores predeterminados son `HeartbeatInterval = 2 s` y `HeartbeatTimeout = 8 s`; detectar un peer silencioso puede tardar además hasta un intervalo de comprobación. Escribir un ping o leer estadísticas locales no demuestra que el peer esté vivo. Los frames de control incompletos también tienen un plazo de lectura. La migración QUIC que conserva el transporte mantiene sus sesiones existentes.

Si el transporte se pierde, la librería puede abrir otra conexión y continuar una sesión ya autorizada. Para declarar que su manejador permite esa continuación, cualquier protocolo implementa:

```csharp
public bool SupportsSessionRecovery => true;
```

`InitQuicConnection` mantiene un único intento lógico por peer/protocolo. Espera a que termine la limpieza del manejador anterior y vuelve a entrar en `HandleAsync` con una conexión y un stream nuevos, conservando `connection.SessionId`; `connection.IsRecoveredSession` distingue una continuación de la primera entrada. El manejador debe respetar la cancelación y conservar o restaurar su estado de aplicación mediante ese identificador. La librería **no reproduce bytes de un stream roto**: el protocolo debe confirmar operaciones o mantener checkpoints si necesita continuar datos pendientes sin duplicarlos. Los multiplexores independientes, construidos sin el coordinador/registro del nodo, mantienen sesiones ordinarias; la recuperación automática se gestiona mediante `InitQuicConnection`.

La recuperación está activada para chat, voz y LAN virtual. El chat recupera su sesión e historial; una transferencia de archivo interrumpida sigue necesitando un nuevo intento de descarga, con verificación íntegra. Los protocolos con operaciones que no pueden repetirse mantienen `SupportsSessionRecovery = false` hasta implementar su estado de continuación. Esta declaración local no negocia versiones ni ofrece un modo de transferencia antiguo.

La autorización de continuación sólo sirve para la misma identidad autenticada, protocolo y sesión lógica, durante `SessionRecoveryWindow` (60 s por defecto desde la pérdida). No abre otra aplicación ni concede un permiso permanente. Una continuación desconocida, expirada o revocada se rechaza sin volver a pedir consentimiento. La limpieza tardía de un transporte anterior no puede borrar la autorización de su reemplazo. El registro tiene límites globales y por peer.

Los intentos de continuación utilizan `SessionRetryDelay = 250 ms`, espera exponencial con jitter y `SessionMaxRetryDelay = 2 s`. La ventana total limita los intentos aunque cada conexión tenga su propio timeout. Una llamada a `CloseProtocolSessionAsync(peerId, protocolId)` cancela también una sesión suspendida; retirar confianza, desconectar al peer o parar/reiniciar el nodo elimina su autorización e intención de recuperación. Cerrar un solo protocolo no cierra las demás sesiones.

`OnSessionStateChanged` informa `Connected`, `Reconnecting`, `Recovered` y `Closed`, con el peer, protocolo e identificador lógico. Los observadores no deben bloquear; sus excepciones se aíslan del transporte. `HasLogicalProtocolSession` incluye las sesiones suspendidas durante su ventana válida.

La librería observa `NetworkChange.NetworkAddressChanged`; el host también puede llamar a `NotifyNetworkChangedAsync` cuando cambie la ruta. Android utiliza el callback de red predeterminada de `ConnectivityManager`. El aviso reinicia las esperas de transporte, despierta los reintentos, cancela negociaciones antiguas, descarta las muestras STUN de la interfaz anterior y reconstruye la señalización WAN. Se conserva la identidad del nodo. Una conexión todavía operativa se prueba antes de dejar que el heartbeat la cierre; el aviso por sí solo no destruye todas las sesiones.

`--test-session-recovery` verifica dos protocolos independientes sobre QUIC nativo, reemplazo del túnel sin repetir consentimiento ni bytes, aislamiento de autorizaciones, caducidad, revocación durante el corte y detección de peers silenciosos aunque las escrituras funcionen. Los cierres normales permiten consumir respuestas ya enviadas antes de cancelar lecturas; el drenaje tiene un plazo común de dos segundos. Las pruebas de IP móvil real siguen siendo necesarias para medir Wi-Fi/datos, NAT y restricciones del operador.
