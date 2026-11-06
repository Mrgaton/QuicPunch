# Plan de permisos para amigos y administración de dispositivos

Fecha: 11 de octubre de 2026. Estado: propuesta; no implementada.

## Objetivo y decisión de diseño

Ofrecer dos perfiles visibles, **Amigo** y **Administrador del dispositivo**, sobre un único motor de autorización reutilizable por QuicNet y otros programas que utilicen QuicPunch. Mantener el transporte P2P existente y distinguir identidad, conexión, sesión de aplicación y acción sobre el dispositivo.

Un administrador es una identidad remota a la que el dispositivo local ha concedido capacidades persistentes. Guardar un peer, compartir una contraseña de conexión o aceptar una llamada no lo convierte en administrador. Tampoco equivale a ser administrador de Windows/Linux.

Ejemplo: el PC autoriza una vez el certificado del móvil. Después, el móvil conecta, abre una sesión de administración y pide iniciar pantalla, voz, LAN o speed test; el PC comprueba su política y ejecuta sin petición local. La autorización PC → móvil no concede automáticamente al PC acceso al móvil.

## Revisión de la implementación actual

La revisión se ha hecho sobre el árbol de trabajo actual, que contiene una reorganización en curso hacia `QuicNet` y `QuicNet.Shared`. No se han revertido cambios ni se ha modificado código ejecutable. Los hallazgos siguientes proceden de lectura de código; no constituyen una validación dinámica.

| Área | Implementación actual | Implicación para el cambio |
| --- | --- | --- |
| Identidad | `QuicPunchConnection.ValidateRemotePeerCertificate` compara SHA3-256 de la clave pública con el hash esperado. `PeerStore` usa ese hash como clave estable. | Conservar esta identidad; los permisos no deben depender de IP, nombre o GUID declarado en una orden. |
| Confianza | `PeerAccessController.IsTrusted` acepta identidades presentes en memoria o guardadas en `PeerStore`. | Confianza y autoridad administrativa necesitan estados independientes. |
| Transporte | `PeerTransportOptions` prepara QUIC para identidades autenticadas sin conceder confianza, con límites, backoff y cierre de inactividad. | Conservar la separación entre transporte y autorización de aplicaciones. |
| Túnel multiplexado | Una conexión física compartida para cada peer; el handshake de transporte no pide aprobación de aplicaciones. | Reutilizar la conexión preparada y autorizar cada sesión virtual por separado. |
| Sesiones | `AuthorizeIncomingSessionAsync` comprueba confianza y permiso automático; si falta, espera decisión local hasta 30 s. El handshake directo tiene una ruta propia. | Reutilizar una sola evaluación tanto en QUIC multiplexado como directo y Tor. |
| Catálogo | `PeerStore.SupportedAutomaticPermissions` contiene seis cadenas; `AutomaticPermissionForProtocol` mapea GUID conocidos en el núcleo. | QuicPunch conoce aplicaciones concretas; falta un registro extensible por programa. |
| UI | `PeerApiModule` repite la comprobación de aceptación automática. | La UI debe presentar decisiones pendientes; el núcleo debe decidir la política. |
| Voz | `VoiceCallHandler` inicia `AudioService` al establecer la llamada; los paquetes locales se distribuyen a llamadas activas. | Separar establecer sesión, capturar audio y autorizar sus destinatarios. |
| Pantalla | La petición remota requiere llamada y `screen-share-request`; sin permiso se deniega. Con permiso, se emite un evento hacia la WebUI. `voice.js` intenta iniciar la captura y muestra el aviso. | No hay una ruta completa de ejecución desatendida independiente del navegador. Tampoco hay una petición interactiva de pantalla para el amigo sin permiso previo. |
| Captura nativa | `FfmpegScreenEncoder` ya ofrece fuentes, perfiles, selección de encoder y `TargetPeerId`; declara captura disponible en Windows. El arranque está integrado en `VoiceApiModule`. | Extraer y reutilizar el servicio existente, sin reconstruir el codificador. |
| LAN | `VirtualLanHandler.HandleAsync` puede arrancar el adaptador bajo demanda si está habilitada la autoasignación. | Hay base para LAN automática; faltan autorización por acción, errores explícitos y control de alcance. |
| Speed test | La sesión recibe `Config` y ejecuta la prueba; limita parámetros, pero abrir la sesión funciona como consentimiento general. | Autorizar cada ejecución y poder cancelarla o limitarla. |
| Ajustes | Los cambios se realizan mediante módulos de API locales y `AppPreferencesStore`. | Falta un protocolo P2P de administración que invoque servicios compartidos. |
| Web local | `WebUiServer` escucha en `127.0.0.1`, comprueba Origin y exige CSRF para operaciones relevantes. | Mantener esa superficie local; transportar órdenes remotas por sesiones P2P autenticadas. |
| QuicWatch | Tiene roles Viewer/Controller/Owner por sala en `RoomCoordinator`, además del permiso de apertura `watch`. | El rol de una sala sigue siendo una autorización de recurso; no concede administración del ordenador. |
| Revocación | Borrar un peer guardado elimina su fila, pero `RemoveSavedPeer` no elimina la confianza de `ExpectedPeerCerts`. `UntrustPeer` tampoco elimina la confianza procedente del store. | Borrar, bloquear, retirar confianza y retirar capacidades necesitan operaciones con efectos definidos sobre sesiones activas. |
| WAN/Tor | Se usan `CertManager` y `TorCertManager` separados. | Una concesión para WAN no debe trasladarse a Tor por coincidencia de nombre; vincular ambas identidades solo mediante emparejamiento explícito. |

Ya hay pruebas útiles en `PeerAccessControllerTests`, `WebUiSecurityTests` y `QuicPeerMultiplexerTests`: confianza, persistencia, aislamiento por peer y rechazo de sesiones. No cubren todavía el modelo completo propuesto.

## Modelo propuesto

### Perfiles y política direccional

Persistir una política asociada a la identidad remota, con `Profile`, política de conexión, reglas por capacidad y revisión monotónica. El perfil aplica una plantilla conocida; las reglas concretas son la autoridad real. Cambiar a Amigo retira las concesiones administrativas de la plantilla anterior, salvo excepciones que el usuario vuelva a elegir explícitamente.

| Capacidad | Amigo, por defecto | Administrador del dispositivo, tras emparejar |
| --- | --- | --- |
| Transporte autenticado | Automático para contactos guardados, sin iniciar aplicaciones | Automático |
| Chat / llamada | Solicitar; puede recordarse permiso | Permitir dentro de la plantilla elegida |
| Capturar micrófono / pantalla / audio del sistema | Consentimiento explícito para esa sesión; puede existir concesión persistente elegida por el propietario | Permitir según las fuentes y destinatarios configurados al emparejar |
| Abrir LAN virtual con ese dispositivo | Solicitar | Permitir |
| Reconfigurar adaptador, rutas o acceso a otras redes | Denegar salvo concesión específica | Permitir únicamente el alcance configurado |
| Ejecutar speed test | Solicitar, con límites | Permitir, con límites |
| Consultar/cambiar ajustes operativos | Denegar | Permitir sobre un catálogo de campos |
| Conceder roles o permisos a terceros | Denegar | Denegar en la primera versión; gestión local del propietario |

Definir `Deny`, `Ask` y `Allow` por capacidad. Precedencia: bloqueo local → identidad autenticada → capacidades disponibles → reglas explícitas y restricciones → concesión temporal válida → decisión interactiva. Una prohibición explícita prevalece sobre una concesión previa.

Una aceptación puntual produce una concesión temporal vinculada a identidad, capacidad, sesión y recurso. No cambia el perfil ni sobrevive al cierre de la sesión. Por ejemplo, aceptar compartir una ventana no permite capturar otro monitor, activar el micrófono o reiniciar captura más adelante sin consentimiento.

La apertura de una llamada interactiva puede presentar en una sola petición las capacidades solicitadas, incluyendo micrófono si procede. Se mantienen decisiones separadas internamente para evitar varias ventanas seguidas y para admitir llamadas de recepción sin captura local.

### Motor común y registro extensible

Ampliar la API pública de seguridad de QuicPunch con un servicio de autorización inyectable. Nombres orientativos:

- `PeerAuthorizationService`: evalúa políticas, emite solicitudes de consentimiento y gestiona concesiones temporales.
- `PeerAuthorizationContext`: identidad tomada de la conexión autenticada, protocolo, sesión, acción y recurso objetivo.
- `AuthorizationDecision`: Allow/Deny/Ask con motivo y revisión de política.
- `CapabilityRegistry` y `ProtocolDescriptor`: capacidades y reglas de apertura registradas por cada programa.
- `IConsentProvider`: adaptador para WebUI, Android, consola u otra interfaz. Si no hay interfaz, Ask termina en rechazo/tiempo agotado; nunca se convierte en Allow.
- `AuthorizationLease`: concesión activa cancelable para una operación o flujo continuo.

Ejemplos de identificadores: `quicnet.voice.session.open`, `quicnet.audio.microphone.capture`, `quicnet.screen.capture.start`, `quicnet.lan.session.open`, `quicnet.lan.settings.write`, `quicnet.speedtest.run`, `quicnet.settings.read` y `quicnet.settings.write`.

Los programas externos registran su propio espacio de nombres. Identificadores estables en disco y protocolo; tipos de C# para evitar cadenas sueltas dentro de cada programa. Una capacidad nueva no queda automáticamente concedida a administradores existentes. La plantilla se versiona y las ampliaciones se eligen localmente.

El núcleo puede autorizar la apertura de sesión. Cada handler debe autorizar además las acciones y los flujos que ejecuta: validar parámetros, comprobar la concesión al comenzar y cancelar inmediatamente al revocarse. Una sesión autorizada no es un permiso universal para todas sus órdenes.

### Emparejamiento administrativo

En la primera entrega, reutilizar el intercambio de tokens existente para localizar/verificar ambos dispositivos y conceder el perfil desde el dispositivo administrado. Mostrar identidad y capacidades concretas. Importar un token normal siempre crea un contacto sin autoridad administrativa.

Para simplificar móvil → PC, añadir posteriormente un QR de emparejamiento de un solo uso, con caducidad, secreto aleatorio y confirmación local que vincule el certificado autenticado del móvil a la concesión. Consumirlo atómicamente; no introducir un indicador `admin=true` en tokens de conexión ordinarios.

Conservar identidades independientes por instalación. Si se quieren autorizar WAN y Tor para el mismo dispositivo, el emparejamiento verifica y registra ambas. Un certificado nuevo requiere nueva concesión o un futuro procedimiento explícito de rotación; no se heredan privilegios por IP o nombre.

### Canal de control remoto

Añadir un `DeviceControlHandler` en `QuicNet.Shared`, registrado como aplicación sobre el multiplexor existente. Mantener los medios, LAN y speed test en sus protocolos actuales. La sesión de control coordina operaciones; las capacidades de cada orden siguen comprobándose en el receptor.

Mensajes versionados: descubrimiento de capacidades, lectura de estado autorizado, orden, resultado, cancelación y cambios de estado. Una orden incluye `RequestId`, acción, parámetros tipados y plazo; la identidad del emisor procede del canal y no del payload.

Resultados explícitos: `Denied`, `ConsentRequired`, `Unsupported`, `Busy`, `Accepted`, `Completed`, `Cancelled` y `Failed`, con identificador de operación cuando no es inmediata. Un timeout de red no demuestra que la acción no se ejecutó.

Deduplicar por identidad y RequestId durante un periodo acotado; preferir órdenes de estado deseado (`SetLanEnabled(true)`) a toggles. Límites de tamaño y concurrencia; validar antes de ejecutar y comprobar de nuevo la revisión de política. No ejecutar órdenes administrativas diferidas desde el mailbox Nostr en esta versión. No habilitar comandos de shell, proxy HTTP genérico ni escritura arbitraria de archivos.

### Servicios de ejecución independientes de la UI

Extraer `ScreenCaptureService`, control de audio, `LanService`, `SpeedTestService` y `DeviceSettingsService`. La API local y el handler remoto llaman a los mismos servicios, con contextos de autorización distintos. El núcleo no necesita referenciar QuicNet ni sus aplicaciones.

Para pantalla desatendida, usar `FfmpegScreenEncoder` desde el servicio, con fuente previamente autorizada y destino concreto. El servicio gestiona la negociación con el receptor y devuelve Unsupported si no puede capturar o codificar. Los permisos propios no sustituyen los requisitos de la plataforma. La ejecución con sesión de Windows bloqueada, tras reinicio o sin escritorio interactivo necesita verificación específica; no debe prometerse por el mero hecho de disponer de FFmpeg.

Para voz, separar abrir el canal de capturar micrófono y de capturar audio del sistema. Aunque se reutilice un dispositivo de captura local, enviar solo a sesiones con concesión activa. Una orden del móvil no debe activar audio para otros amigos conectados ni anular silenciamientos ajenos a su alcance.

Para LAN, separar conectarse a la red virtual de habilitar rutas hacia la LAN física, bridge o forwarding. La plantilla básica permite una sesión virtual con el peer autorizado; otros destinos y cambios del adaptador requieren reglas concretas. Devolver errores de privilegios del sistema, sin diálogos de elevación repetidos como mecanismo de autorización P2P.

Para ajustes, publicar un esquema de campos operativos permitidos, con límites y control de revisión. Excluir secretos, certificados, roles y concesión de permisos. Cambiar WAN/Tor puede cerrar el canal de administración: responder con una operación identificada y aplicar con una secuencia de reconexión prevista, o rechazar cambios que eliminen la única ruta sin alternativa configurada.

Las operaciones capturando pantalla o audio se ligan a sesiones activas y se detienen al perder autorización o destinatario. El adaptador LAN es un recurso compartido: cerrar la sesión revocada no debe detener otras sesiones legítimas. Los ajustes persistentes confirmados permanecen; retirar permisos impide cambios posteriores, sin deshacer automáticamente configuraciones previas.

## Conexión y revocación

Conservar descubrimiento, STUN, hole punching, multiplexer, Tor y validación criptográfica. Un perfil no garantiza conectividad: el equipo debe estar encendido y el proceso escuchando. Arranque automático del agente es un trabajo de host separado; el acceso antes del inicio de sesión y Wake-on-LAN no forman parte de esta primera entrega.

Centralizar la autorización en todas las rutas: handshake directo, túnel, sesión virtual, órdenes remotas y handlers heredados. La resolución de conexiones simultáneas puede reconocer una intención local equivalente, pero `wasYielded` no concede acciones adicionales ni sobrevive a una revocación.

Mantener separados `AutoConnect` (intentar conectar), política de conexión entrante y permisos de aplicación. La configuración global actúa como valor por defecto; una regla explícita del peer puede reemplazarlo. El modo inseguro de aceptación de desconocidos, si se conserva para pruebas, no concede capacidades de dispositivo.

La autorización utiliza una identidad inmutable vinculada al transporte autenticado, no el `PeerInfo` mutable de descubrimiento. En Tor se obtiene de su mecanismo de autenticación existente; no se presupone un certificado TLS donde se usan transportes emulados.

Al revocar capacidades: incrementar revisión, cancelar concesiones/operaciones afectadas, cerrar sesiones que ya no sean válidas y rechazar órdenes en cola. Al bloquear o eliminar un peer: limpiar store, confianza en memoria, excepciones de autoaceptación, solicitudes pendientes y transportes activos. Añadir un bloqueo explícito para impedir que un redescubrimiento o intento ya en marcha restaure acceso.

La persistencia de permisos necesita control de revisión bajo el bloqueo de archivo. `PeerStore.Save` mezcla disco y memoria y el watcher recarga cambios externos: un proceso con estado antiguo no debe sobrescribir una revocación reciente. Actualizar la política mediante comparación de revisión y escritura atómica; rechazar conflictos y mantener tombstones para eliminaciones si varias instancias comparten el store. Al arrancar, un error de lectura de políticas no debe conceder acceso.

## Implementación por fases

| Fase | Trabajo y archivos principales | Criterio de finalización |
| --- | --- | --- |
| 1. Base de autorización | `QuicPunch/Security`, `QuicPunch/Helpers/PeerStore.cs`, `QuicPunchBuilder.cs`. Introducir perfiles, reglas, registro de capacidades, revisiones y concesiones temporales. | La misma identidad obtiene la misma decisión desde cualquier aplicación; campos desconocidos no conceden acceso. |
| 2. Unificar conexiones | `QuicPunch.cs`, `PacketHandler/HandshakeHandler.cs`, `Multiplexer/QuicPeerMultiplexer.cs`, rutas Tor, `WebUi/Modules/PeerApiModule.cs`. Retirar decisiones duplicadas y concretar revocación. | Mismo comportamiento en multiplexado y directo; bloqueo efectivo aun con sesiones abiertas. |
| 3. Separar ejecución | `QuicNet.Shared/Protocols/VoiceCallHandler.cs`, `Video/FfmpegScreenEncoder.cs`, módulos Voice/Lan/SpeedTest, `Settings/AppPreferencesStore.cs`, `QuicNet/Protocols/VirtualLanHandler.cs`. Extraer servicios y controlar destinatarios. | La ejecución autorizada no necesita la WebUI; amigos conservan consentimiento y aislamiento. |
| 4. Control remoto | Nuevo `DeviceControlHandler` y cliente compartido; registro en `QuicNet/Program.cs` y `QuicPunch.AndroidProbe/AndroidConnectionService.cs`. Órdenes tipadas, resultados y compatibilidad. | Móvil autorizado inicia pantalla, voz, LAN y prueba, consulta/cambia ajustes y recibe el resultado real. |
| 5. Emparejamiento y UI | `PeerApiModule`, `StatusApiModule`, `NodeCapabilities`, UI de peers/dashboard/voice y host Android. Dos perfiles, editor de capacidades, emparejamiento y estado remoto. | Concesión inicial local, acciones posteriores sin peticiones; UI distingue permiso de disponibilidad. |
| 6. Migración y pruebas finales | `PeerStore`, pruebas de seguridad, sesiones y handlers, documentación y soporte del host. | Ningún contacto existente se eleva a administrador; reinicio conserva políticas y revocaciones. |

La primera entrega útil puede usar concesión administrativa local mediante tokens ya existentes y dejar el QR como mejora de la fase 5. No requiere cambiar el handshake criptográfico ni reescribir el hole punching.

## Migración y compatibilidad

Crear una versión nueva del esquema, conservando lectura de v1/v2. Todos los peers existentes se migran a Amigo. Preservar las concesiones automáticas elegidas previamente y mostrarlas como excepciones explícitas, sin convertirlas en autoridad administrativa.

Mapear `chat`, `voice`, `vpn`, `speedtest`, `watch` y `screen-share-request` a sus equivalentes registrados. `voice` conserva el comportamiento de audio previamente autorizado, dentro del alcance de la llamada; `screen-share-request` conserva la concesión previa para pantalla, con sus requisitos de sesión y fuente. `vpn` no concede nuevas rutas hacia redes físicas. Desglosar control y medios de Watch conservando las reglas de sala.

No inferir Admin de `AutoConnect`, la conexión QUIC, confianza, contraseña de grupo o permisos automáticos. Escribir el esquema nuevo con copia de respaldo y rechazar escrituras desde clientes antiguos que no entiendan esa versión.

Negociar versión y capacidades del protocolo de control. Si el receptor antiguo no lo soporta, mostrar esa limitación y ofrecer los flujos existentes de forma explícita; nunca sustituir una orden administrativa por una ruta que omita autorización.

## Validación necesaria

1. Matriz de Unknown/Blocked/Friend/Admin × Deny/Ask/Allow × transportes y protocolos. Abrir una sesión no basta para ejecutar una acción protegida.
2. Un amigo no inicia pantalla, micrófono ni cambia ajustes sin concesión válida. El consentimiento puntual no se reutiliza para otro recurso, sesión o peer.
3. El móvil emparejado actúa sin petición local con la WebUI cerrada; pantalla/audio solo llegan al destinatario autorizado, incluso con otras llamadas abiertas.
4. Revocar mientras hay captura, speed test, LAN, solicitud pendiente o reconexión cancela lo afectado. Borrar y volver a añadir la misma identidad no restaura permisos anteriores.
5. Persistencia, migración v1/v2, cambios externos y dos instancias concurrentes: ninguna escritura antigua resucita capacidades revocadas.
6. Órdenes duplicadas, timeout y reconexión no ejecutan dos veces. Parámetros inválidos, demasiadas solicitudes, campos de ajustes fuera del catálogo y capacidades desconocidas se rechazan.
7. Emparejamiento caducado/reutilizado, identidad distinta, uso de nombre/IP ajenos y vínculo WAN/Tor no autorizado no producen administración.
8. Probar pantalla Windows con navegador cerrado, fuentes autorizadas, disponibilidad de FFmpeg/encoder y sesión bloqueada; publicar los escenarios realmente compatibles. Android actúa como cliente según sus capacidades.
9. Mantener permisos de sala de QuicWatch y funcionamiento de chat, voz, LAN y speed test existentes. Verificar resolución simultánea y cancelación del handshake con las políticas nuevas.

Extender las suites existentes y añadir pruebas específicas de autorización por acción, leases y control remoto. Ejecutar al implementar los cambios `--test-peer-acl`, `--test-webui-security`, `--test-multiplexer`, `--test-handshake-cancellation`, `--test-screen`, y las pruebas de voz, speed test y LAN afectadas. Para este documento no se han ejecutado pruebas ni se ha afirmado que el nuevo diseño esté implementado.

## Resultado esperado

El propietario empareja su móvil una vez con el PC, concede las acciones deseadas y puede ejecutarlas remotamente. Los amigos mantienen consentimiento por sesión y recurso, con excepciones persistentes elegidas conscientemente. Cualquier programa que use QuicPunch puede registrar sus capacidades y aplicar el mismo modelo sin incorporar su propia lista de amigos/administradores ni duplicar la lógica de autorización.
