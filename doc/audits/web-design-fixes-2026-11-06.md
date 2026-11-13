# Corrección de la auditoría de diseño del 6 de noviembre de 2026

Los **20 hallazgos de la auditoría original están corregidos y verificados** en el código local. Se han conservado los cambios previos del proyecto. No se ha publicado una versión ni modificado contactos reales.

[Auditoría original](web-design-2026-11-06.md) · [Capturas posteriores](design-fixes-2026-11-06/index.html) · [Evidencias y código examinado](design-fixes-2026-11-06/evidence.zip)

| ID | Corrección | Verificación |
|---|---|---|
| 01 | El sondeo del disco conserva el borrador de la ruta hasta cambiar de ubicación, equipo o carpeta. | Escritura, pérdida de foco y sondeo de 5 segundos sin perder el texto. |
| 02 | Enviar archivos y Enviar carpeta son botones reales que abren sus selectores. | Ambos abren el selector con Enter. |
| 03 | Los contactos guardados conservan sus controles y los permisos desplegados al actualizar el estado. | Cambiar el nombre mediante una actualización conserva el mismo elemento enfocado y el desplegable abierto. |
| 04 | Un cambio rechazado de rol en Ver juntos recupera el rol confirmado. | Respuesta 403: vuelve a Espectador y permanece así después del siguiente sondeo. |
| 05 | Colgar tiene su propia zona fija; los demás controles pueden desplazarse. | Llamada activa a 844×390, 667×375 y 320×568: botón habilitado, dentro de la ventana y sin obstáculos. |
| 06 | Los comandos y enlaces largos de la ayuda pueden partirse en varias líneas. | A 320 px ningún comando rebasa su contenedor y la página no desborda. |
| 07 | Las descargas distinguen un fallo de consulta de la ausencia de versiones y ofrecen Reintentar. | HTTP 503 y fallo de un solo canal, recuperación sin recargar y conservación de las versiones verificadas disponibles. |
| 08 | El texto de error de descargas tiene mayor contraste. | Color #ffa7b3 sobre #1b252b: 8,49:1, superior a 4,5:1. |
| 09 | El perfil permite reintentar la carga y vuelve a intentarlo al regresar a la categoría. | Fallo 503 seguido de Reintentar: se recuperan el nombre y los controles sin recargar. |
| 10 | WAN y Tor ofrecen un botón de registro cuando hay un error. | Enter abre Diagnóstico → Registro de eventos, con el registro visible y enfocado. |
| 11 | Las confirmaciones y mensajes de guardar, eliminar y cambiar puerto están en español. | Confirmación de eliminación comprobada y revisión de los textos asociados. |
| 12 | El cuadro de mensajes conserva las sugerencias sin usar aria-expanded, incompatible con el textarea. | Comprobación del DOM y análisis de accesibilidad de la conversación. |
| 13 | Las regiones tienen nombre, las tablas tienen encabezados de acciones y las listas y títulos usan semántica válida. | Comprobaciones de DOM, incluido el título accesible en conversación móvil, y matriz axe sin infracciones. |
| 14 | Administrador exige una confirmación que explica el acceso antes de enviar la petición. | Cancelar recibe el foco; Escape no envía ninguna petición; Conceder envía exactamente una. |
| 15 | Los filtros secundarios y el modo de conexión están en un desplegable; el aviso móvil ocupa menos espacio. | A 390×844, Mensaje y Llamar del primer contacto quedan por encima de la navegación inferior. |
| 16 | Contactos y permisos comienza por Guardados; los detalles de conexión se agrupan debajo. | Guardados visible de entrada, estados correctos y pruebas de categorías y pestañas. |
| 17 | Herramientas abre accesos a Red privada, Velocidad y Ajustes en móvil; el chat ofrece el mismo menú. | Abrir Herramientas y navegar a Velocidad en móvil. |
| 18 | El volumen maestro tiene una zona interactiva de 44 px de altura. | Medición de la geometría del control en el navegador. |
| 19 | Vídeo empieza por resolución, fluidez y datos; motor y códecs están en opciones avanzadas. | Calidad visible de entrada; motor oculto hasta abrir Formato y codificación avanzada; suite FFmpeg aprobada. |
| 20 | Descargas explica cómo abrir el EXE o instalar el APK según la plataforma. | Guías Windows y Android y publicación técnica dentro de un desplegable secundario. |

La suite permanente [DesignAuditBrowserTests.mjs](../../QuicPunchTests/Tests/DesignAuditBrowserTests.mjs) cubre los 20 puntos. También se actualizaron seis pruebas existentes para abrir «Filtros y conexión» antes de usar sus controles; sus comprobaciones funcionales se mantienen.

**Resultado de la verificación:** 21/21 suites web rápidas y 19/19 suites de navegador seleccionadas aprobadas. Las suites de navegador fueron DesignAudit, SettingsNavigation, VirtualDisk, VoiceRoom, WatchRoom, DownloadPage, ContactHome, ContactReview, ContactSignals, HomeCall, WorkspaceLayout, ChatUsability, ChatOverview, FfmpegTuning, Updates, DarkInterface, ConnectionModes, ChatIsolation y DiscoveryAvailability.

La matriz visual final recoge **76 estados**, sin desbordamiento horizontal del documento ni errores JavaScript registrados. Se ejecutó axe-core 4.11.0 en **51 estados** con las etiquetas WCAG 2 A/AA, 2.1 AA, 2.2 AA y buenas prácticas: cero infracciones detectadas. Las ventanas principales fueron 1440×900, 768×1024, 390×844, 320×568 y 844×390; los ajustes también se revisaron a 390×900. Se revisaron visualmente las capturas de inicio móvil, llamada horizontal, contactos y calidad de vídeo.

Los resultados usan Chromium y servidores locales de prueba. Las respuestas de permisos, perfil, disco y canales de descarga se simulan para reproducir los fallos; la identidad de firma de la prueba se genera únicamente en memoria. Esto verifica la interfaz y sus interacciones, no llamadas reales entre dispositivos ni instalaciones en Windows/Android. Un análisis automático de accesibilidad tampoco sustituye una evaluación completa con tecnologías de asistencia.

Comandos para repetir las comprobaciones principales desde la raíz:

```powershell
node scripts/test-web.mjs --web
node scripts/test-web.mjs --browser --filter=DesignAudit
node scripts/test-web.mjs --browser --filter=SettingsNavigation
node scripts/test-web.mjs --browser --filter=Contact
```

Los resultados y las capturas de trabajo se generan en `.artifacts/design-fixes-2026-11-06/`. La copia de evidencias enlazada arriba se conserva en `doc/audits/` y no depende de que sobreviva esa carpeta temporal. El archivo incluye el script de la matriz, axe, las fuentes web examinadas y las fixtures necesarias; requiere instalar las dependencias de Node y Chromium del proyecto para repetirlo.
