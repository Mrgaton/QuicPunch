# Plan de navegación móvil de QuicNet

## Decisión

La barra inferior quedará en este orden.

**Inicio · Mensajes · Llamadas · Ver juntos · Ajustes · Más**

«Más» irá al extremo derecho y abrirá un panel con una sola pulsación. Mantener Ajustes como acceso directo conserva un lugar estable para el perfil, las notificaciones y los permisos. El cambio usa las seis posiciones de la barra actual y da acceso a las herramientas desde la zona inferior de la pantalla.

El panel mostrará estas opciones, en orden.

1. **Disco virtual** — Envía archivos y abre carpetas compartidas.
2. **Red privada** — Conecta tus dispositivos en una misma red.
3. **Prueba de velocidad** — Mide la velocidad y la latencia con un contacto.
4. **Tu estado** — Muestra el estado actual y abre su selector.

Disco virtual irá arriba para facilitar el cambio a quienes ya lo abren desde la barra. El acceso pasará de una a dos pulsaciones. Si se usa con frecuencia, conviene observar primero el uso del menú y la facilidad para encontrarlo antes de reconsiderar su posición.

Se reutilizará el diálogo de Herramientas con aspecto de panel inferior en móvil vertical y de diálogo centrado y desplazable en horizontal. En las pantallas con barra inferior visible, «Más» ocupará el lugar del acceso «Herramientas» de la cabecera. Cuando una conversación o el teclado oculten esa barra, el menú de la conversación seguirá ofreciendo acceso al mismo panel. En escritorio y en los tamaños que usan la barra lateral, Disco virtual conservará su enlace directo y se mantendrán los accesos correspondientes a ese diseño.

## Implementación

1. **Definir la navegación en el marcado.** Declarar el enlace de escritorio de Disco virtual en `index.html` y retirar su inserción dinámica de `virtual-disk.js`. Añadir el botón «Más» con el icono de puntos y la etiqueta visible. Usar el mismo punto de corte de la barra inferior, 680 píxeles CSS, para mostrarlo y para ocultar allí el enlace directo de Disco virtual. Mantener `/virtual-disk.html` y sus parámetros como entradas válidas para enlaces, marcadores y accesos desde contactos.
2. **Reutilizar el panel y su gestión.** Ampliar el diálogo existente en `index.html` y centralizar los disparadores en `design-usability.js`. Las herramientas usarán enlaces reales y la navegación de QuicNet, igual que hoy. «Tu estado» seguirá siendo un botón. Si el sistema declara una herramienta no disponible, aplicar la misma regla de disponibilidad que en la barra lateral. Una herramienta disponible que requiera un permiso conservará su acceso y mostrará las instrucciones de su pantalla.
3. **Representar la pantalla actual.** Inicio, Mensajes, Llamadas, Ver juntos y Ajustes conservarán su selección habitual. Dentro de Disco virtual, Red privada o Prueba de velocidad, «Más» indicará visualmente que la pantalla pertenece al grupo. El enlace exacto dentro del panel llevará `aria-current="page"`. Limitar la selección de rutas de `app.js` a los enlaces de la barra y actualizar el indicador de grupo desde el código del panel. Ambas indicaciones responderán a la navegación, a los enlaces directos y a Atrás o Adelante. Elegir la pantalla que ya está abierta solo cerrará el panel.
4. **Hacer que el panel funcione con toque, teclado y TalkBack.** Reutilizar `<dialog>` y dar al botón `aria-haspopup="dialog"` y `aria-controls`. Mantener el foco dentro del diálogo, llevarlo al botón Cerrar al abrir, devolverlo al disparador al cancelar y cederlo al encabezado de la pantalla al elegir una herramienta. Si el disparador queda oculto al girar el dispositivo, usar el enlace activo o el encabezado de la pantalla. Al abrir «Tu estado», cerrar primero el panel y aplicar la misma restauración al cerrar el selector. Usar áreas táctiles de al menos 44 × 44 píxeles CSS, respetar las áreas seguras y permitir desplazar el contenido en horizontal o con el teclado abierto.
5. **Validar Atrás en el WebView de Android.** La apertura del panel será un estado de interfaz. La navegación solo añadirá una entrada al elegir un destino distinto. Comprobar en un teléfono que Atrás cierra primero el panel y conserva la pantalla actual. Si el WebView envía el gesto directamente al historial, integrar el cierre del diálogo en el manejo de Atrás de `MainActivity.cs`, respetando la prioridad de la pantalla completa y el teclado.

## Criterios de aceptación

| Escenario | Resultado esperado |
| --- | --- |
| Inicio en un móvil vertical | Cinco enlaces directos y el botón «Más». Disco virtual se abre en dos pulsaciones y «Herramientas» queda integrado en la barra. |
| Entrada directa a Disco virtual, Red privada o Velocidad | «Más» indica el grupo activo y el panel identifica la pantalla exacta al abrirse. Los enlaces a un contacto o carpeta conservan sus parámetros. |
| Cerrar el panel o pulsar Atrás | Se conserva la pantalla, el desplazamiento y el estado de sus controles. El foco vuelve a un control visible. |
| Conversación con la barra inferior oculta | El menú de conversación permite abrir el mismo panel. Volver a la lista devuelve el acceso a «Más» en la barra. |
| Cambiar de orientación con el panel abierto | Las opciones y Cerrar siguen al alcance. Al cerrar, el foco vuelve a un acceso visible en la nueva disposición. |
| Herramienta no disponible en el dispositivo | La barra lateral y el panel aplican el mismo criterio de disponibilidad. |
| Escritorio y barra lateral | Disco virtual se sigue abriendo directamente. Las demás entradas mantienen su orden y estado activo. |

Actualizar `PhoneLayoutBrowserTests.mjs` para distinguir cinco enlaces y un botón, comprobar que el panel abre en una pulsación y que todos sus destinos caben y se pueden tocar. Ampliar `AccessibleToolsBrowserTests.mjs` para cubrir foco, pantalla actual, enlaces directos, navegación sin recarga y restauración al cerrar. Revisar también `ChatUsabilityBrowserTests.mjs` y `VirtualDiskBrowserTests.mjs` para comprobar los accesos desde conversaciones y contactos.

Capturar Inicio, el panel abierto y Disco virtual en 320 × 568, 390 × 844 y 667 × 375. Revisar también el salto a barra lateral y el giro a 844 × 390. Cerrar la validación con TalkBack y el gesto Atrás en un Android físico.

## Validación de la implementación

- Chromium cubrió las seis entradas de la barra, el acceso a Disco virtual en dos toques, las rutas con parámetros, el estado activo, la disponibilidad de herramientas, el foco, la rotación y el acceso desde una conversación. Se generaron las nueve capturas previstas.
- MuMu con Android 15 sustituyó al teléfono físico. Con la APK instalada, Atrás cerró el teclado, cerró el panel conservando el historial y volvió a la pantalla anterior después de elegir Disco virtual. TalkBack FOSS recorrió dos enlaces consecutivos y cerró el panel con su gesto Atrás. MuMu desactiva el gesto lateral del sistema en esta instancia.
- Pasaron las 21 suites web y 54 de las 56 suites de navegador. Los dos fallos reproducidos antes de este cambio corresponden a la restauración de borradores y la selección de códec.
