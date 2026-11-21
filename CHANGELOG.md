# Changelog de QuicNet

## 1.2.0 (Build 31) · 22 de noviembre de 2026

- **Audio, vídeo y red privada.** WinMM admite ráfagas de finalización de búferes del controlador. El visor conserva el encuadre ampliado al ocultar sus controles. La VPN espera a que el adaptador y el túnel puedan recibir tráfico. Las aperturas simultáneas mantienen un solo túnel y los primeros paquetes viajan por el flujo fiable hasta que el otro equipo confirma que recibe datagramas.
- **Android e interfaz.** El permiso de micrófono se solicita al usarlo y el de avisos desde Notificaciones. Actualizaciones distingue la versión instalada de la publicada. Disco virtual y Mensajes ofrecen acceso directo al alta de contactos. Los controles de llamada aparecen al iniciar la sesión y los rótulos del menú de escritorio son más legibles.
- **Control remoto.** El menú de la llamada permite iniciar la captura y solicitar el control en un solo paso. Los contactos con perfil de administrador pueden recibir ratón y teclado automáticamente. Al revocar el perfil se retira el control, y los demás contactos necesitan confirmación local.
- **Captura en Windows.** La pantalla compartida puede componer varios monitores mediante DXGI y usar codificación nativa NVENC en equipos compatibles.
- **Contactos y estado.** Se redujo el trabajo necesario para actualizar el panel de estado con muchos contactos. La interfaz reutiliza la información de red de cada actualización y mantiene compatibles los datos que reciben los clientes.
- **Ver juntos.** Escritorio y Android comparten el arranque y el cierre de la función, incluidos el registro de protocolos y la liberación de sus recursos.

## 1.1.26 (Build 29) · 19 de noviembre de 2026

- **Control remoto.** En el menú del contacto durante una llamada, **Solicitar control remoto** puede iniciar la captura y pedir el control en un solo paso. Si el dispositivo receptor tiene al contacto guardado como administrador, concede ratón y teclado automáticamente y retira ese control si se revoca el perfil. Los demás contactos requieren confirmación local. Android sigue solicitando el permiso del sistema para capturar la pantalla completa.

## 1.1.23 (Build 26) · 19 de noviembre de 2026

- **Pantalla compartida.** Corregida la pantalla negra al maximizar vídeo en Android. El visor permite ampliar con la rueda o con dos dedos, arrastrar la imagen y volver al encuadre original. La barra de controles se oculta tras tres segundos de inactividad. El selector de códec refleja inmediatamente los cambios entre CPU y GPU.
- **Control remoto.** Solicita el control de un escritorio compartido desde Windows, Android o Linux X11. La persona que comparte aprueba cada sesión y puede detenerla en cualquier momento. El visor añade teclado completo, selección entre los monitores compartidos y portapapeles de texto bidireccional con permiso de ambos equipos.
- **Compatibilidad.** Android requiere activar el servicio de accesibilidad de QuicNet y compartir toda la pantalla para recibir control. La escritura en campos de texto requiere Android 13 o posterior. La captura y el control remoto en Linux están disponibles en X11.
- **Mensajes y llamadas.** Los borradores se guardan por conversación en el dispositivo. Puedes abrir, compartir o usar una invitación desde Inicio, Mensajes o Llamadas y volver al selector con el contacto incorporado.
- **Interfaz.** Nuevo icono de QuicNet. El menú **Más** reúne Disco virtual, Red privada, Prueba de velocidad y Tu estado en móviles. Se reorganizaron los controles y mensajes de ayuda de estas herramientas y de Ver juntos.
- **Actualizaciones.** Se muestra una fase de verificación diferenciada. Puedes cambiar la preferencia de instalación automática durante la descarga. Si la desactivas antes de que empiece la instalación, la descarga sigue disponible para instalarla manualmente.

## 1.1.22 (Build 24) · 18 de noviembre de 2026

- La actualización automática espera 15 minutos de inactividad y se aplaza mientras haya una llamada en curso. Si se vuelve a usar el dispositivo durante la descarga, el paquete verificado queda preparado para la siguiente pausa.
- Windows mide la inactividad de la sesión. Android cuenta el tiempo con el dispositivo en reposo.
- Desde Android 12, QuicNet puede solicitar la instalación en segundo plano. Cuando Android exige permiso o confirmación, el paquete queda listo para instalar desde Ajustes.
- El panel de actualizaciones muestra cuándo la app espera a que el dispositivo quede inactivo. Se mantiene la instalación manual desde Ajustes.

[Cambios desde 1.1.21](https://github.com/Mrgaton/QuicPunch/compare/c373b91a48c6b119d73ff11c2c70162f2857a38b...terror)
