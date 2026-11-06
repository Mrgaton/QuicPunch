# Permisos automáticos por peer y aplicación

Los permisos se configuran en **Peers guardados → Permisos automáticos**. Se guardan junto a la identidad del peer (hash del certificado), se conservan al actualizar sus direcciones y desaparecen al borrarlo. Las concesiones explícitas empiezan desactivadas. El chat directo está siempre disponible para identidades de confianza y no necesita una petición de aceptación, incluso para contactos ya existentes sin permiso `chat` guardado. El descubrimiento no utiliza estos permisos.

| Permiso | Solicitud que puede aceptarse automáticamente |
| --- | --- |
| Chat directo | Apertura sin petición para cualquier identidad de confianza; no es una concesión opcional |
| Llamadas | Apertura de una sesión de voz |
| VPN / LAN | Apertura de una sesión LAN Bridge, cuando está disponible |
| Prueba de velocidad | Apertura de una sesión de prueba |
| QuicWatch | Apertura de sesiones de control y transferencia de medios |
| Disco virtual | Apertura del canal; solo permite acceder a las carpetas compartidas y nunca concede exploración del sistema |
| Pedir mi pantalla | Petición durante una llamada activa para iniciar mi pantalla |
| Cambiar ajustes de mi transmisión | Cambios remotos de calidad sobre una pantalla que ya estoy enviando a ese peer |

Una sesión entrante de una aplicación distinta del chat sin permiso sigue pasando por la petición local con un límite de 30 segundos. QUIC se prepara automáticamente para los peers descubiertos con identidad autenticada, sin conceder confianza. La autorización se aplica al abrir cada protocolo sobre el transporte existente; los peers sin confianza no pueden abrir aplicaciones. La política y sus límites se describen en [Transporte automático de peers](peer-transport.md). Esto se aplica tanto a las sesiones virtuales sobre una conexión QUIC como a las conexiones directas heredadas.

El permiso **Pedir mi pantalla** requiere una llamada activa, pero no concede por sí solo el permiso **Llamadas**. Sin ese permiso previo, la petición presenta un aviso local y el usuario debe pulsar **Elegir pantalla y compartir**. Con permiso previo, la captura nativa puede iniciarse automáticamente y se dirige únicamente al peer solicitante. El navegador puede exigir un clic local y la selección de ventana o monitor mediante `getDisplayMedia`; en ese caso queda una acción visible en Voice Studio para completar la petición. La aplicación no puede omitir esa decisión del navegador con un permiso propio.

## Amigos y administradores del dispositivo

Cada peer guardado tiene un perfil **Amigo** (predeterminado) o **Administrador de este dispositivo**, seleccionable en Peers guardados. La concesión es direccional: elegir administrador en el PC permite a esa identidad actuar sobre el PC, sin conceder autoridad recíproca. El esquema del store es v4; contactos v1/v2 se migran a Amigo y conservan sus permisos explícitos. La migración de v3 mantiene sus perfiles y confianza.

La confianza se guarda por separado mediante `Trusted`. `UntrustPeer` retira autoridad y cierra las sesiones de aplicación conservando el contacto, sus endpoints y sus preferencias. La revocación persiste tras reiniciar y no se deshace al actualizar direcciones. `TrustPeer` vuelve a conceder confianza explícitamente. Borrar solo la persistencia de un contacto no retira una confianza concedida explícitamente en memoria; para revocar autoridad se utiliza UntrustPeer. Los permisos y perfiles conservados no tienen efecto mientras el contacto carezca de confianza. Una versión antigua que no conoce el esquema v4 no puede reescribir el store omitiendo una revocación.

Un administrador acepta automáticamente las sesiones de aplicaciones registradas por el programa, incluyendo protocolos externos al mapa de QuicNet, y dispone de los permisos de pantalla y ajustes de transmisión. La confianza y el perfil no se obtienen del descubrimiento ni del nombre/IP. El perfil se conserva al actualizar endpoints. Cambiar a Amigo retira las concesiones implícitas, manteniendo las excepciones elegidas manualmente. No equivale a privilegios de administrador del sistema operativo ni añade ejecución de comandos o una API genérica para cambiar cualquier ajuste del equipo.

`POST /api/peer/profile` configura `certHash` y `profile` (`friend`/`administrator`) mediante la WebUI local protegida por Origin y CSRF. `/api/status` publica el perfil. Importar un token nunca concede el perfil administrador.

En [Disco virtual](../protocols/virtual-disk.md), el administrador también puede explorar todos los discos y carpetas accesibles para el proceso y compartírselos sin una nueva aceptación. Un amigo necesita una carpeta compartida por el propietario; aceptar la sesión abre la selección local de carpeta y no comparte automáticamente el disco. Los permisos de lectura, escritura, borrado y caducidad se validan en el equipo que comparte en cada operación.

## Control remoto de pantalla en una llamada

Cada llamada ofrece **Pedir pantalla** y **Transmisión**. El segundo botón consulta al emisor: backend actual, códecs/encoders probados, resoluciones máximas, FPS, rangos de bitrate/calidad/GOP/buffer, permisos y configuración actual. La interfaz filtra encoders por códec y habilita calidad VBR solo para NVENC disponible. La captura web ofrece los controles que su backend implementa.

El receptor envía ajustes y revisión esperada; el emisor valida permiso, destinatario, valores, disponibilidad y revisión. No se permite cambiar remotamente la fuente ni los destinatarios de una captura existente. Un perfil nativo negocia su configuración exacta con los decodificadores antes de emitir. La captura web aplica el cambio en la pestaña que posee la captura, negocia el perfil y confirma el resultado; otras pestañas no alteran esa transmisión. La respuesta devuelve el perfil efectivo, incluyendo resolución/FPS reales. Un fallo devuelve error; nunca se confirma éxito antes de la aplicación. Un cambio concurrente requiere volver a consultar las opciones.

Las órdenes/respuestas viajan como frames `0x29`/`0x2A` por la llamada autenticada, correlacionadas por identidad y RequestId. La versión de control se anuncia como noveno byte opcional de las capacidades de pantalla, conservando los ocho bytes originales. Un cliente anterior mantiene la petición básica de pantalla y muestra que necesita actualizarse para configurar remotamente.

La captura administrativa nativa funciona sin WebUI en el emisor cuando Windows y FFmpeg disponen de un encoder operativo; el receptor sigue necesitando su decodificador. Si solo hay captura web, se necesita la acción local requerida por el navegador. Retirar el perfil/permisos impide nuevas órdenes y detiene la captura nativa iniciada por ese peer si ya no tiene permiso para pedir pantalla. Los ajustes locales y remotos nativos se serializan para evitar reinicios simultáneos.

La API local usa `POST /api/voice/screen-share/remote` (`peerId`, `action`, `revision`, `settings`) para consultar/aplicar; los endpoints `browser-state` y `browser-result` publican estado y confirmación del backend web. Ninguno se expone fuera del servidor local.

El endpoint local `POST /api/peer/automatic-permission` recibe `certHash`, `permission` y `allowed`. `/api/status` publica `automaticPermissions` para mostrar el estado. La petición remota de pantalla viaja por la sesión de voz autenticada; no existe un endpoint de red abierto para activar la captura.

Para comprobar persistencia, perfiles y migración: `QuicPunchTests --test-webui-security`. `--test-screen` prueba frames y control remoto sobre sesiones reales: permisos, revisión, códecs disponibles, configuración aplicada, fallo del navegador y retirada de autoridad. `--test-peer-acl`, `--test-multiplexer` y `--test-ffmpeg-screen` cubren los componentes correspondientes.
