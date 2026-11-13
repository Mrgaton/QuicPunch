# Ver juntos y el espacio de trabajo

La interfaz comparte la paleta, tipografía, controles y navegación de
`QuicNet.Shared/wwwroot/workspace.css`. Se carga después de los estilos de cada
función. Los identificadores y contratos existentes se conservan para que las
llamadas, mensajes, ajustes y herramientas sigan usando sus servicios actuales.
La navegación indica la página activa, conserva el menú contraído y ofrece
controles táctiles, foco visible, movimiento reducido y colores forzados.

La sala de vídeo tiene HTML y CSS propios, sin importar el diseño de la ventana
principal dentro del iframe. Su disposición separa el reproductor, la preparación
de la sala, los contactos y la invitación. En móvil usa una columna; con vídeo
activo recupera la proporción 16:9. El iframe ocupa el espacio libre tras las
llamadas y la navegación, sin hacer crecer la ventana exterior.

## Android

Android registra los mismos protocolos de control y medios que el escritorio.
Puede crear salas, invitar, asignar control y recibir el vídeo por rangos.
El selector usa Storage Access Framework (`ACTION_OPEN_DOCUMENT`, `video/*`),
sin permisos de acceso general al almacenamiento. Los proveedores pueden ser
remotos o no admitir saltos: para alojar un vídeo se prepara una copia temporal
en la caché privada de la aplicación, manteniendo intacto el original.

La importación usa un búfer de 128 KiB, reserva 64 MiB libres y respeta el límite
de medios de 64 GiB. Un error o una cancelación conserva el vídeo anterior. Al
aceptar un reemplazo se retiran las copias antiguas. Cerrar el servicio cancela
la importación y elimina las copias desechables. Abrir el selector no detiene
el servicio aunque la preferencia de ejecución en segundo plano esté desactivada.

## Flujo

1. Crear una sala con nombre y, opcionalmente, contactos seleccionados.
2. Elegir un vídeo; en Android se abre el selector del sistema.
3. Invitar contactos y asignarles permiso de espectador o controlador.
4. Reproducir con el estado confirmado por el anfitrión. Cuando el navegador
   bloquea autoplay, aparece una acción explícita para unirse a la reproducción.

La selección indica actividad y bloquea acciones repetidas. Cancelar el selector
no es un error. El servidor comprueba propiedad antes de abrirlo y rechaza el
resultado si la sala cambió durante la selección. Android no admite rutas manuales.
Las instantáneas de reproducción se aplican tras cargar metadatos y los espectadores
no envían cambios de control. Los mensajes de caché se filtran por vídeo y las
revisiones antiguas del mismo medio no sustituyen a las nuevas.

## Verificación

- `dotnet build QuicPunch.AndroidProbe/QuicPunch.AndroidProbe.csproj` con SDK Android
  y JDK 21 configurados genera la APK en las carpetas estándar `bin/`.
- `dotnet run --project QuicPunchTests -c Test -- --test-watch-reliability` comprueba
  los canales, rangos, cachés, permisos e identidades compartidos con Android.
- `node QuicPunchTests/Tests/WatchRoomBrowserTests.mjs` comprueba creación, selección,
  cancelación, error de almacenamiento, metadatos de un MP4 real, autoplay,
  permisos de espectador, navegación y tamaños de 320 a 1920 píxeles.
- Las capturas y el vídeo generado de prueba se guardan en `.artifacts/ui-review/`.
  Son desechables y no contienen datos ni identidades reales del usuario.

La reproducción depende de los códecs del WebView/navegador. El alojamiento en
Android necesita espacio libre para una copia del original. No se ha validado en
esta tarea una sesión audiovisual entre un teléfono físico y un escritorio.

Referencias de implementación: [Storage Access Framework de Android](https://developer.android.com/guide/topics/providers/document-provider)
y [tamaño de objetivos de WCAG 2.2](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum).
Estas pautas orientan el diseño; no representan una certificación completa de accesibilidad.
