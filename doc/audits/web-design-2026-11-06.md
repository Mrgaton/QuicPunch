**Investigación de diseño y usabilidad de QuicNet — 6 de noviembre de 2026**

Resultado: **20 hallazgos: 13 defectos reproducidos o comprobados en el DOM y 7 problemas de usabilidad y jerarquía visual.** Los problemas de mayor impacto afectan a conservar lo que se está editando, operar con teclado y representar correctamente los permisos. Esta investigación no modifica la aplicación ni propone que los avisos automáticos equivalgan a una certificación de accesibilidad.

La revisión final corresponde a una copia de los archivos tomada a las **21:51:33, Europe/Madrid**. Durante la investigación hubo modificaciones concurrentes en la web, ajenas a esta auditoría. Para no mezclar versiones, se repitieron las comprobaciones sobre esa copia. Los hallazgos describen esa versión; los números de línea de archivos que sigan cambiando pueden desplazarse.

[Galería de evidencias seleccionadas](design-2026-11-06/index.html) · [Evidencias y copia reproducible de la web](design-2026-11-06/evidence.zip) · [Huella SHA-256 de los archivos revisados](design-2026-11-06/source-manifest.json)

**Alcance y método**

Se revisaron las superficies activas del proyecto, no las copias históricas de `backups/`, los bocetos de diseño ni los proyectos retirados de `precached/`.

| Superficie | Cobertura |
| --- | --- |
| Inicio y contactos | Agenda con datos, agenda vacía, búsqueda y filtros, invitación, edición, permisos y confirmación de eliminación |
| Mensajes | Vista general, conversación con mensajes y texto largo, estado desconectado, navegación móvil, semántica del compositor |
| Llamadas | Sala vacía y llamada simulada, orientación horizontal, paneles de audio, vídeo y recepción |
| Red privada | Formulario, dispositivos y estado vacío, distribución en escritorio y móvil |
| Velocidad | Configuración, resultados sin iniciar e historial vacío; distribución en varios tamaños |
| Disco virtual | Vista general, carpetas y archivos ficticios, escritura de rutas, refresco automático y controles de subida |
| Ajustes | Las ocho categorías y las siete subpestañas de contactos y diagnóstico; edición de perfil, error de carga y recorte de foto |
| Ver juntos | Vista integrada y página independiente, sala vacía y creada, nombres largos y error al cambiar permisos |
| Descargas y ayuda | Página preparada para descargar, fallo del servidor de versiones y guía de publicación |

Se generaron **102 capturas**. La matriz principal contiene 78 vistas/estados. Se utilizaron Chromium mediante Playwright, mediciones del DOM, inspección visual, lectura del código y axe-core 4.11.0. Los tamaños principales fueron 1440×900, 768×1024, 390×844, 320×568 y 844×390; se añadieron 667×375 y estados a 390×900. Las pruebas se sirvieron en loopback con respuestas ficticias. No se iniciaron conexiones P2P, VPN, llamadas, cámara o micrófono reales; no se cambiaron permisos de contactos reales ni se descargó una publicación real.

Las prioridades son de producto: **P1**, interacción bloqueada, pérdida de edición o estado de permisos engañoso; **P2**, dificultad importante, recuperación insuficiente o contenido inaccesible; **P3**, problemas de consistencia y semántica de menor impacto. No se ha trasladado automáticamente la gravedad que asigna axe a la prioridad de producto.

**Defectos comprobados**

**01 · P1 · Disco virtual borra una ruta mientras se escribe.**

Al seleccionar una carpeta compartida, escribir `carpeta/que/estoy/escribiendo` en «Ubicación» y esperar 5,3 segundos sin pulsar «Ir», el campo vuelve a quedar vacío. `refresh()` termina llamando a `controls()`, que asigna a `diskPath.value` la ruta confirmada, aunque el usuario esté editando. No depende de una mala conexión: se reprodujo con respuestas correctas del servidor simulado. Esto hace que una tarea normal dependa de terminar antes del siguiente refresco. Referencia: `QuicNet.Shared/wwwroot/virtual-disk.js:68`, temporizador en las líneas 211 y 213. Evidencia: `deep-results.json`, registros `disk-path-before-poll` y `disk-path-after-poll`.

**02 · P1 · Enviar a la carpeta abierta no tiene un control utilizable con teclado.**

«Enviar archivos» y «Enviar carpeta» se implementan como etiquetas con aspecto de botón que contienen un `input type=file` oculto. La etiqueta tiene `tabIndex=-1`; el selector tiene `display:none`. No hay un botón ni un manejador de teclado equivalente para cargar en la carpeta seleccionada. «Compartir archivos», en la parte superior, inicia otro flujo: prepara una carpeta de intercambio, no sube a la ubicación abierta. El arrastre tampoco sustituye el teclado. Referencias: `views/virtual-disk.html:27` y `virtual-disk.js:227`. [Captura de la carpeta abierta](design-2026-11-06/deep-disk-populated.png). El requisito de operar las funciones mediante teclado se explica en [WCAG 2.1.1](https://www.w3.org/WAI/WCAG22/Understanding/keyboard.html).

**03 · P1 · Los permisos de contactos se cierran y pierden el foco con cada actualización de estado.**

En Ajustes → Contactos y permisos → Guardados, desplegar «Permisos automáticos» y enfocar una casilla. Al emitir una actualización de estado idéntica, desaparece el nodo enfocado, el foco pasa a `BODY` y quedan cero desplegables abiertos. `renderSaved()` vacía y reconstruye todas las filas. Afecta tanto a quien está leyendo los permisos como a quien los recorre con teclado. No se atribuye a producción el intervalo de diez segundos del servidor de pruebas: el fallo ocurre cuando llega una actualización, cualquiera que sea su frecuencia. Referencia: `dashboard.js:633`. Evidencia: `deep-results.json`, `saved-permissions-refresh`.

**04 · P1 · Ver juntos mantiene seleccionado un permiso que el servidor ha rechazado.**

Con una sala propia y una persona con permiso «Espectador», cambiar el selector a «Control» y responder 403 a `/watch/api/permission`. Se muestra el error, pero el selector permanece en `Controller`. Tras otro refresco que sigue devolviendo `Viewer`, la selección continúa incorrecta y el aviso ya ha desaparecido. La clave `peerRenderKey` evita reconstruir la fila porque el estado confirmado no ha cambiado; el error no restaura el valor modificado localmente. La interfaz induce a creer que se aplicó una autorización distinta de la real. Referencia: `QuicNet.Shared/Watch/wwwroot/app.js:63`, 78 y 116. [Estado tras desaparecer el error](design-2026-11-06/deep-watch-role-error-lost.png).

**05 · P2 · El botón para finalizar una llamada queda fuera de la vista en horizontal.**

En una llamada simulada a 844×390, el botón habilitado `voiceLeaveBtn` empieza en `y=432` y termina en `y=476`: queda completamente por debajo de la ventana. A 667×375 empieza en `y=416`. La barra lateral sí tiene desplazamiento interno; por tanto, el botón es recuperable desplazando esa barra o enfocándolo con teclado, pero una acción principal queda escondida sin una indicación clara. El cuerpo de la página tiene `overflow:hidden`. Referencia: `voice-room.css`, reglas de `.voice-dock`, `.voice-quick-controls` y `@media(max-height:620px)`. [Llamada a 844×390](design-2026-11-06/deep-active-call-844.png).

**06 · P2 · Los comandos de la ayuda se recortan en móvil.**

Abrir `updates-help.html` a 320 px. Los dos bloques de comandos tienen 194 px de ancho y contenidos de 616 y 816 px. Los `pre` mantienen `overflow:visible`, pero la sección contenedora recorta el excedente. No aparece desplazamiento horizontal general ni un desplazamiento útil del bloque: la parte final del comando no se puede leer visualmente. Esta es una razón concreta por la que comprobar solamente `scrollWidth <= innerWidth` no detecta todos los problemas de adaptación. Referencia: `updates-help.html:9`, 10 y `.section` en `design.css`. [Captura](design-2026-11-06/edge-help-320.png).

**07 · P2 · La descarga confunde un fallo del servidor con la ausencia de versiones y no ofrece reintentar.**

Responder 503 a los manifiestos de versiones, manteniendo disponible la clave pública de prueba. La página muestra simultáneamente «No hay una versión disponible para esta selección» y «No se pudo consultar el canal stable». El botón de descarga queda desactivado y «Reintentar» está oculto. El reintento se muestra únicamente cuando hay una URL de verificación del navegador, no ante errores ordinarios de red. Cambiar dispositivo o canal no vuelve a consultar el servidor; hace falta recargar la página. Referencia: `scripts/download-page/download.js:11`, 15 y 62. [Captura](design-2026-11-06/deep-download-error.png).

**08 · P2 · El mensaje de error de descarga tiene contraste insuficiente.**

El texto de error de 13 px usa `#bd3548` sobre `#1b252b`: **2,79:1**, medido por axe. Precisamente la explicación de por qué no funciona la descarga resulta difícil de leer. La referencia para texto normal es 4,5:1, con las excepciones que recoge [WCAG 1.4.3](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html). No se está denunciando el contraste del botón desactivado, que tiene otro tratamiento normativo. Referencia: `scripts/download-page/download.css:1`, regla `#status.error`. Misma captura que el hallazgo 07.

**09 · P2 · Un error al cargar el perfil deja el formulario bloqueado sin recuperación dentro de la categoría.**

Simular un error en `/api/profile`, recuperar el servicio y cambiar de «Mi perfil» a otra categoría y volver. El nombre y su botón siguen desactivados y permanece el error anterior. No hay «Reintentar»; cambiar categorías emite `settings_section_changed`, mientras la carga del perfil escucha `view_changed`. Salir de Ajustes y volver, o recargar, permite otro intento, pero esa salida no se explica. Referencia: `profile.js:21`, 44 y `settings-navigation.js`. [Captura del error](design-2026-11-06/edge-profile-error.png).

**10 · P2 · Pulsar el error de WAN o Tor no abre el registro al que intenta dirigir.**

En Conexión y Tor, presentar un `lastError` y pulsar el texto del estado. El código intenta hacer `scrollIntoView()` sobre `#logs`, pero ese registro está dentro de Diagnóstico de red → Registro de eventos, ambos ocultos desde esa vista. Se comprobó que la categoría seguía siendo `connection` y que `#logs` no era visible. Además, la interacción se asigna a texto mediante `onclick` y `cursor:pointer`, no a un control de navegación reconocible. Referencia: `dashboard.js:205` y 220. Evidencia: `edge-results.json`, `service-error-navigation`.

**11 · P2 · Las confirmaciones para eliminar contactos cambian al inglés.**

Pulsar «Eliminar» en Guardados abre `Delete saved peer "Ana · Portátil"? This removes it from the persistent database.`. La limpieza de desconectados también pregunta en inglés. El cambio de idioma ocurre justo cuando se debe comprender el alcance de una eliminación; además, «base de datos persistente» describe la implementación en lugar de la consecuencia para la persona. Se abrió y canceló el diálogo sobre datos ficticios. Referencia: `dashboard.js:853` y 854. También quedan mensajes como `peer(s) saved` y `Listener moved to` en las líneas 921 y 923.

**12 · P2 · El compositor del chat utiliza un atributo ARIA incompatible con su función.**

El `textarea#messageInput` lleva `aria-expanded`, atributo que no corresponde al rol implícito `textbox`. axe lo detecta cuando el compositor está visible y habilitado. Las sugerencias de menciones y su estado abierto/cerrado necesitan una representación coherente para las tecnologías de asistencia. Esto confirma un problema de semántica; no se ha probado con un lector de pantalla que impida escribir ni se ha asumido esa consecuencia. Referencia: `views/chat.html:111`. Evidencia: `results.json`, `390-chat-conversation`, regla `aria-allowed-attr`.

**13 · P3 · Varias regiones y tablas tienen semántica incompleta o ambigua.**

Se detectan regiones complementarias sin nombres que las distingan en Mensajes y Ajustes; encabezados vacíos en las tablas avanzadas; `aria-label` sobre `div#diskFiles` sin un rol que admita ese nombre; y `article role=listitem` en las tarjetas de contactos. En la conversación móvil también desaparece el único `h1` al ocultar la columna de conversaciones. Son problemas distintos agrupados por su impacto en la orientación semántica, no cinco bloqueos independientes. Referencias: `index.html` (`aside.nav-wrap`), `views/chat.html`, `views/settings.html:222`, 256, 335 y 354, `views/virtual-disk.html:25`, y `experience.js` al crear tarjetas. Las reglas, selectores y fragmentos concretos están en `results.json`. No todas estas reglas tienen carácter de incumplimiento WCAG; varias son buenas prácticas.

**Problemas de usabilidad y jerarquía visual**

Estos siete puntos se apoyan en observación o mediciones, pero su valoración como problema de diseño requiere juicio de producto. No se presentan como errores de ejecución ni como resultados de una prueba con usuarios.

**14 · P1 · El alcance del perfil Administrador se explica después de concederlo.**

El selector «Amigo / Administrador de este dispositivo» guarda inmediatamente al cambiar. Solo tras responder la API aparece la explicación de que ese contacto puede iniciar aplicaciones, controlar la pantalla y explorar y compartir discos sin nuevas peticiones. Se interceptó la petición ficticia y se confirmó este orden. Antes de elegir faltan una explicación visible del alcance y una transición suficientemente deliberada. El problema es la información previa a una decisión sensible; no implica que todos los ajustes deban pedir confirmación. Referencia: `dashboard.js:640–650`. [Vista de permisos](design-2026-11-06/390-tab-saved-peers.png).

**15 · P2 · En Inicio móvil, los controles y avisos desplazan la acción principal fuera de la primera pantalla.**

Con tres contactos y uno pendiente de revisión, a 390×844 los botones «Mensaje» y «Llamar» del primer contacto aparecen en `y≈927`. Antes se muestran modo WAN/Tor, dos acciones de invitación, estado de descubrimiento, configuración de conexión, tres filtros, buscador, estado de conexión, orden y un aviso que repite la revisión pendiente. La tarea cotidiana de hablar con alguien exige desplazarse pese a que la agenda es pequeña. [Captura completa](design-2026-11-06/390-home.png). Es una valoración de jerarquía, no un argumento para eliminar información técnica de todo el producto.

**16 · P2 · «Contactos y permisos» obliga a atravesar varias listas antes de llegar a los permisos.**

La categoría empieza por contactos conectados, contactos disponibles y conexiones en curso; solo después aparece el directorio con Guardados y sus permisos. Con dos contactos guardados, la vista móvil alcanza **2835 px**. La misma persona se representa en el bloque de conectados y otra vez en Guardados, con acciones y términos diferentes. El título de la categoría promete una tarea que queda enterrada tras información de diagnóstico. [Captura](design-2026-11-06/390-tab-saved-peers.png). Referencia: `views/settings.html:201` en adelante.

**17 · P2 · Red privada y Velocidad cambian de ubicación de forma poco evidente en móvil.**

Al bajar de 680 px desaparecen de la navegación principal. Los accesos alternativos existen, pero se encuentran dentro de Ajustes → Conexión y Tor → Herramientas, por debajo de otros controles; en la prueba el bloque estaba en `y≈882`. No se afirma que sean inaccesibles: el problema es descubrir dónde fueron dos herramientas que en escritorio son destinos principales. Referencia: `design.css:105` y `views/settings.html:199`.

**18 · P2 · El volumen maestro tiene un área vertical muy pequeña para interacción táctil.**

En los ajustes de audio, `masterVolumeSlider` mide **318×6 px** a 390 px de ancho. El control equivalente de volumen de notificaciones reserva 44 px de alto. Esta inconsistencia hace más difícil tocar la pista del volumen de una llamada. La medida de la pista no demuestra por sí sola un incumplimiento de tamaño mínimo: el pulgar y las excepciones por separación deben valorarse también. [WCAG 2.5.8](https://www.w3.org/WAI/WCAG22/Understanding/target-size-minimum.html) distingue esos casos. Evidencia: `results.json`, `390-media-audio`.

**19 · P3 · La primera pantalla de ajustes de vídeo pide decisiones técnicas antes de ofrecer una elección sencilla.**

«Motor de captura», «Este navegador · WebCodecs», «Códec y procesamiento», GPU y la comparación con FFmpeg ocupan la primera vista móvil. Aunque hay ayudas, quien quiere compartir una pantalla debe interpretar conceptos de implementación antes de llegar a opciones de calidad y consumo. Es una carga cognitiva, especialmente desde un botón general «Ajustes», sin que la pantalla se identifique como avanzada. [Captura](design-2026-11-06/deep-video-settings-mobile.png). No se considera fallo el estado «comprobando», porque la prueba no completa una sesión multimedia real.

**20 · P3 · La descarga ofrece documentación de publicación, pero apenas acompaña la instalación.**

La página presenta un enlace «Publicar actualizaciones» y explica Zstandard y verificación, mientras solo Linux tiene instrucciones específicas. Para Windows y Android la salida principal es «Ya puedes guardarlo e instalarlo», sin pasos posteriores por plataforma. Mezcla información para quien mantiene el proyecto con la tarea de quien intenta empezar a usarlo. Esto no prueba un fallo del instalador: es una carencia de orientación en la página. Referencia: `scripts/download-page/index.html` y `download.js`.

**Qué no se debe deducir de esta investigación**

No apareció desbordamiento horizontal global en las 78 vistas de la matriz final. Eso no descarta recortes internos: el hallazgo 06 los demuestra. La prueba inicial de apertura de una foto AVIF no completó el flujo en el entorno de prueba; se repitió con PNG y se pudo revisar el recorte a 320×568. No se ha atribuido el primer resultado a un fallo del producto.

No se contabilizaron como defectos las versiones vacías de Actualizaciones, los datos ausentes de geolocalización ni las comprobaciones multimedia incompletas que produce el servidor ficticio. Tampoco se mantuvieron observaciones iniciales de Ver juntos que dejaron de aplicar tras las ediciones concurrentes: la copia final sí incluye una acción de preparación en el reproductor, validación del nombre y reintento de conexión.

No se verificaron Safari/iOS, Firefox, un Android físico, lectores de pantalla reales, teclado virtual del sistema, zoom nativo del navegador, sincronización entre dos usuarios, transferencias reales, audio/vídeo activos ni la descarga desplegada en un CDN público. La revisión de estados de error valida la respuesta de la interfaz a fallos controlados; no mide la frecuencia de esos fallos en producción. «Exhaustiva» aquí significa una revisión amplia de todas las superficies activas identificadas, no una garantía de ausencia de otros problemas.

**Reproducción y conservación de evidencias**

El archivo `evidence.zip` conserva la copia revisada de la web, los tres scripts de investigación, los resultados JSON y las 102 capturas. No contiene claves privadas, credenciales ni datos de contactos reales. La identidad criptográfica empleada para simular manifiestos de descarga se generó solo en memoria y no se guardó.

Para repetir la investigación desde la raíz del repositorio, extraer el archivo en `.artifacts/design-audit-2026-11-06/`, disponer de las dependencias Playwright del proyecto y de su Chromium, e instalar temporalmente axe con `npm pack axe-core@4.11.0 --pack-destination .artifacts/design-audit-2026-11-06`, seguido de `tar -xf .artifacts/design-audit-2026-11-06/axe-core-4.11.0.tgz -C .artifacts/design-audit-2026-11-06`. Ejecutar `node .artifacts/design-audit-2026-11-06/audit.mjs`, después `deep-audit.mjs` y `edge-audit.mjs` desde la misma carpeta de trabajo. Los scripts utilizan la copia archivada, no el frontend que siga evolucionando.

Los resultados duraderos están en `doc/audits/`. `.artifacts/` contiene únicamente la copia de trabajo, dependencias temporales y resultados regenerables; puede limpiarse siguiendo las instrucciones del proyecto.
