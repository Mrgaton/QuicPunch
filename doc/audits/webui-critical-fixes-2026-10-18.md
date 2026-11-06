# Correcciones de la auditoría web · 18 de octubre de 2026

Cada fallo se corrigió y se comprobó por separado antes de continuar con el siguiente. Las pruebas usan contactos, archivos y audio/vídeo sintéticos; no utilizan las conversaciones ni los dispositivos personales del usuario.

| Fallo | Corrección | Verificación |
| --- | --- | --- |
| Ejecución desde un adjunto antiguo | La descarga solo acepta las URL de datos aprobadas por el filtro. Se retiraron también las alternativas que recuperaban datos rechazados en imágenes, audio y vídeo. | `WebUiCriticalBrowserTests.mjs --case=downloads`, con la CSP de producción y una descarga válida. |
| Cambio accidental de destinatario | Los adjuntos y cada sesión de nota de voz conservan el contacto elegido al iniciar la operación. La nota muestra su destinatario. | `--case=recipient`: cambio de conversación durante FileReader y durante una grabación nativa sintética. |
| Cámara tras finalizar una llamada | Se invalida la apertura pendiente, se cierran las pistas que llegan tarde y se comprueba la vigencia de la operación después de las esperas. El servidor rechaza iniciar vídeo sin llamadas. | `CameraBrowserTests.mjs`: colgado local, remoto y desaparición de la llamada del estado; `--test-screenshare`. |
| Micrófono retenido por notas | Cada grabación posee sus pistas, fragmentos y temporizador; las pistas se liberan al enviar, cancelar, salir del chat o fallar. Los permisos tardíos se invalidan y no se admiten aperturas duplicadas. | `--case=microphone`: MediaRecorder y pistas reales generadas en memoria; comprobación de estado `ended`. |
| Bloqueo con Markdown | El reconocimiento de código en línea recorre los delimitadores sin retroceso combinatorio. También se evita el recorrido repetido de corchetes sin cerrar. | `ChatFormattingWebTests.mjs`, textos adversarios con límite de tiempo y `--case=markdown` en el DOM. El mensaje de 60 KB pasó de unos 4,5 s a 7–8 ms en este equipo. |
| Llamada pendiente sin cancelación | El estado se conserva hasta una respuesta real, fallo o cancelación. Colgar cancela también el intento de conexión previo a la sesión. Un permiso tardío no inicia una llamada cancelada. Los errores de colgado permiten reintentar sin resucitar llamadas ya finalizadas. | `--case=calls`, reloj simulado, fallos de transporte y prueba HTTP de cancelación en `--test-webui-security`; `--test-session-recovery`. |
| Descarte de adjuntos múltiples | Los envíos se serializan por destinatario mediante promesas; un error libera la cola y permite continuar con los demás archivos. La pérdida de conexión se comunica. | `--case=attachments`: primera petición retenida, tres archivos, concurrencia máxima uno y fallo del primero sin perder el siguiente; `ChatOutboxBrowserTests.mjs`. |
| Sala de vídeo sin salida | El propietario dispone de «Cerrar sala» y el espectador de «Salir de la sala». La API y el protocolo eliminan la sala o al participante, notifican a los demás y permiten crear otra sala. | `--case=watch`: propietario/espectador, error y reintento, recarga y nueva sala. Pruebas HTTP y de autorización en `--test-webui-security`. |

## Problemas adicionales corregidos durante las pruebas

- Los menús del chat podían desaparecer por una confirmación o una actualización de reacciones. Las filas que no cambian permanecen adjuntas al DOM; las filas actualizadas conservan el menú abierto y el foco. Se comprueba con `--case=menus` y `ChatSocialBrowserTests.mjs`.
- La entrega remota del vídeo ahora exige pertenecer a la sala actual y solicitar su vídeo activo. Salir o cerrar revoca el acceso a nuevos rangos. Las pruebas verifican que un tercero no puede cerrar una sala ni leer su vídeo.
- Las operaciones simultáneas de crear/salir de sala se excluyen y las tareas de reproducción diferida verifican también la sala y el vídeo, además de la revisión.

## Ejecución

```powershell
node QuicPunchTests/Tests/WebUiCriticalBrowserTests.mjs
node QuicPunchTests/Tests/ChatFormattingWebTests.mjs
node QuicPunchTests/Tests/CameraBrowserTests.mjs
dotnet build QuicPunchTests/QuicPunchTests.csproj -c Test --no-restore
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-webui-security
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-screenshare
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-session-recovery
```

Los scripts de navegador permiten especificar `QUICNET_PLAYWRIGHT_MODULE` y `QUICNET_TEST_BROWSER_PATH`. La regresión incluye también funciones sociales del chat, mensajes pendientes, escritura, pantalla, selector de fuentes, sala de llamadas y distribución adaptable. Las pruebas de cámara ejercitan la ruta Android con agente de usuario y captura sintéticos; no sustituyen una comprobación física de permisos o hardware. El Chromium utilizado omite el decodificador H264, por lo que esa previsualización concreta se informa como omitida, mientras la captura WebCodecs compatible sí se ejercita.

La compilación y las pruebas del servidor se ejecutaron con .NET 11. La compilación finalizó sin errores y conservó seis advertencias de nulabilidad preexistentes en `SecurityDiscoveryTests.cs`.
