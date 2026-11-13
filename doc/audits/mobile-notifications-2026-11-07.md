# Revisión móvil y notificaciones — 7 de noviembre de 2026

## Alcance comprobado

Recorrido visual en Chromium con emulación táctil de las ocho vistas principales: inicio/contactos, conversaciones, llamadas, ver juntos, disco virtual, red privada, velocidad y ajustes. Revisadas las ocho categorías de ajustes, la navegación, herramientas, edición de contactos y la conversación con un viewport reducido por el teclado. Se probaron anchos de 320, 360, 390 y 430 px, además de horizontal y pantallas cortas. Las suites existentes también comprueban escritorio y estados de error, espera, cancelación y contenido largo.

**No es una prueba física de Android.** Durante esta revisión ADB solo detectó una Quest 2. No se instaló la compilación ni se verificó sonido, vibración, entrega con pantalla bloqueada o comportamiento del fabricante en un teléfono. La emulación no reproduce la gestión de batería ni los permisos reales de Android. La aplicación sigue necesitando su servicio de conexión activo para recibir mensajes; estos cambios no incorporan un servicio push que despierte una aplicación detenida.

## Cambios

- **Sonidos por categoría.** Mensajes, archivos y pantalla usan secuencias diferentes; los avisos generales tienen un tono corto y las llamadas conservan el timbre de dos pulsos. Android incluye cinco WAV estables; escritorio usa las mismas formas de onda. `scripts/generate-notification-sounds.py` regenera los recursos y la prueba compara su PCM con el generador C#.
- **Canales Android audibles.** Los avisos del sistema delegan sonido y vibración en Android, sin una segunda reproducción con MediaPlayer. Las reglas de la aplicación que silencian un aviso seleccionan un canal silencioso. Se conservan los bloqueos y la importancia de canales antiguos, y la selección explícita de sonido cuando Android permite distinguirla. Los canales nuevos no se recrean para sobrescribir decisiones posteriores del usuario.
- **Llamadas.** El timbre manual respeta permiso, canal, volumen, modo silencio y No molestar. En modo vibración puede vibrar sin reproducir audio. Al aceptar, rechazar o cancelar una petición se detiene el timbre y se retira su aviso Android. Una lista acotada de avisos retirados impide que un envío retrasado lo vuelva a mostrar. El aviso de llamada tiene también caducidad de seguridad.
- **Permisos visibles.** Ajustes muestra si Android permite o silencia los avisos, permite abrir sus ajustes y actualiza el estado al volver. La prueba informa de un bloqueo del sistema en vez de afirmar que envió el aviso. Con notificaciones Android activas, la selección del tono se hace en Android y el control local identifica expresamente el volumen del timbre. Se explica cómo mantener la recepción al salir de la aplicación.
- **Privacidad y destinos.** Las notificaciones Android tienen visibilidad privada en la pantalla bloqueada y texto expandible. Los intents tienen identidad propia por aviso, evitando colisiones entre conversaciones. La actividad conserva el nuevo intent y rechaza rutas con barras invertidas. Los avisos dentro de la interfaz tienen acciones para abrir la conversación, llamada o archivo; solo admiten destinos del mismo origen.
- **Presentación Android.** Nombre visible QuicNet e icono monocromo propio para sus avisos y servicio. El indicador persistente dice «QuicNet activo», sin afirmar que ya existe una conexión con otro contacto. El diálogo inicial de permisos no provoca el cierre del servicio cuando el segundo plano está desactivado.
- **Espacio y controles móviles.** En un chat de hasta 680 px de ancho y 520 px de alto, mientras el editor tiene foco se ocultan temporalmente el menú inferior y la fila cerrada de opciones de historial. Se recupera espacio para leer y enviar; la navegación vuelve al perder el foco o ampliar el viewport. Ampliados a 44 px el cierre de avisos, sus acciones, el acceso de cabecera, el desplegable de detalles VPN y el deslizador de volumen. Los avisos accionables permanecen ocho segundos y pausan su caducidad con foco o puntero.

## Validación

Resultado: **12 suites de navegador correctas**, sin errores de página en las comprobaciones correspondientes:

| Suite | Cobertura relevante |
|---|---|
| MobileNotificationExperienceBrowserTests | Estado Android, bloqueo de prueba, apertura de ajustes, regreso, enlaces seguros, ocho categorías a tres tamaños y teclado |
| MobileNotificationsBrowserTests | Avisos arriba, cierre accesible, mensajes largos, horizontal y viewport de teclado |
| PhoneLayoutBrowserTests | Ocho vistas, navegación, controles táctiles y nombres largos de archivos en cinco tamaños |
| ChatUsabilityBrowserTests | Editor, historial, errores, multimedia, navegación y estados de llamada |
| SettingsNavigationBrowserTests | Categorías, foco, historial, recarga y tamaños de pantalla |
| AccessibleToolsBrowserTests | Herramientas, diálogos, etiquetas, errores y foco |
| VoiceRoomBrowserTests | Controles, participantes, paneles, medios y diseño adaptable |
| WatchRoomBrowserTests | Capacidades Android, creación, selección, cancelación y errores |
| VirtualDiskBrowserTests | Selectores Android simulados, transferencias, revocación y diseño |
| SpeedTestBrowserTests | Inicio, cancelación, errores, historial y tamaños |
| VpnBrowserTests | Edición, reintento, estados mixtos y diseño |
| TableLabelPerformanceBrowserTests | Etiquetas de tablas al convertirse en tarjetas móviles |

`--test-notifications` pasa: políticas y privacidad, reglas persistidas, denegación del sistema, contrato JSON real, protección CSRF al abrir ajustes, tonos/recursos, cadencia, ausencia de timbres simultáneos y cancelación/retirada de avisos.

Compilaciones `Test` de escritorio/pruebas y Android correctas. El APK contiene los cinco WAV y el icono nuevo. Permanecen los seis avisos CA1416 preexistentes del probe Android y los avisos de nulabilidad preexistentes de las pruebas de seguridad. No se ha publicado una versión.

Capturas de fixtures: [chat con teclado](mobile-notifications-2026-11-07/keyboard.png), [ajustes de avisos](mobile-notifications-2026-11-07/system-settings.png), [aviso accionable](mobile-notifications-2026-11-07/notice-action.png).

## Repetición

```powershell
dotnet build QuicPunchTests -c Test
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-notifications --verbose
node scripts/test-web.mjs --browser --filter=MobileNotification
node QuicPunchTests/Tests/PhoneLayoutBrowserTests.mjs
dotnet build QuicPunch.AndroidProbe -c Test
```

La prueba física pendiente debe comprobar permiso concedido/denegado, modo normal/vibración/silencio, canal bloqueado, pantalla bloqueada, apertura de dos conversaciones distintas desde sus avisos y retirada inmediata al finalizar una llamada pendiente. Las preferencias de No molestar y volumen del usuario deben conservarse.

Referencias oficiales utilizadas: [canales Android y control del usuario](https://developer.android.com/develop/ui/compose/notifications/channels), [permiso de notificaciones](https://developer.android.com/develop/ui/compose/notifications/notification-permission), [Notification.Builder](https://developer.android.com/reference/android/app/Notification.Builder).
