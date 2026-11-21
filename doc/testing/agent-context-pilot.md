# Evaluación del trabajo de agentes en QuicNet

Mide la tasa de entregas válidas y el coste de llegar a ellas. La comparación inicial usa dos copias de la misma revisión. A conserva el `AGENTS.md` raíz y B coloca el [candidato](../architecture/plantilla-agents.md) en esa ruta. Documentación, binarios iniciales y cachés deben ser equivalentes. El [plan](../architecture/plan-mantenimiento-ia.md) describe una comparación adicional para el mapa y el runner con instrucciones idénticas.

## Ocho tareas

La columna «Encargo» es el único enunciado que recibe el agente en cada par. La columna «Comprobación» es para quien evalúa. Cada ejecución empieza desde el mismo contenido y con una sesión nueva. Las tareas son pilotos para checkouts aislados.

| ID | Encargo | Comprobación |
| --- | --- | --- |
| ui-nav | En el listado de contactos añade una forma de restablecer la búsqueda y el filtro de conexión sin alterar el orden elegido. Debe poder usarse con teclado a 320 px. | La lista vuelve a mostrar todos los contactos, el orden persiste tras recargar y el foco permanece utilizable. Ejecuta `ContactHomeBrowserTests.mjs`. |
| chat-draft | Conserva el texto más reciente de un borrador si el primer guardado falla mientras el usuario ya tiene otra conversación visible. Reintenta al volver a la conversación. | La respuesta tardía y el reintento afectan al contacto original. Comprueba una petición pendiente controlada y ejecuta `ChatNavigationWebTests.mjs`. |
| status | Añade a estado el número de protocolos registrados en `registeredProtocolCount` sin cambiar `registeredProtocols`. | El valor coincide con el tamaño de la lista en HTTP, `status.get` y los eventos de estado. Comprueba el contexto JSON y ejecuta `webui-status,aot-json`. |
| screen-picker | Recuerda el último filtro de fuentes dentro de la misma página sin compartir estado entre ventanas. | Reabre la selección tras cancelar y comprueba el filtro. En otra página nueva debe empezar en todas las fuentes. Ejecuta `ScreenPickerBrowserTests.mjs`. |
| reconnect | Expón un contador de recuperaciones de sesión completadas, legible desde la misma instancia del nodo. | Una recuperación válida incrementa una vez. Un intento cancelado o rechazado no incrementa. Ejecuta `session-recovery`. |
| watch-host | Permite cambiar la ubicación de la caché de QuicWatch desde el objeto que monta los protocolos para un host. La ubicación predeterminada del usuario debe seguir funcionando. | Instancia con una ruta temporal, verifica qué ruta crea y comprueba que ambos protocolos se retiran al cerrar. Ejecuta `watch-reliability`. |
| updates | Muestra el canal destino en el mensaje de confirmación de actualización durante una llamada. | Comprueba beta y estable, cancelación, foco y consentimiento. Ejecuta `UpdatesBrowserTests.mjs`. |
| runner | Añade una forma de seleccionar suites JavaScript por nombre exacto además del filtro parcial. | Un nombre exacto devuelve solo esa suite, un nombre ausente falla y el filtro anterior sigue disponible. Comprueba listado, ejecución y código de salida. |

Fija los fixtures del evaluador antes de las corridas y mantenlos fuera del checkout del agente. Comprueba el diff para descartar salidas simuladas y cambios que salten comprobaciones. Inspírate en las suites indicadas para preparar cada condición de aceptación. Si un encargo ya se completó en la base escogida, sustitúyelo antes del piloto en ambas variantes.

## Ejecución y registro

1. Registra la revisión o el identificador del snapshot, el agente y su versión, el modelo, las herramientas, el límite de tiempo y el estado inicial de caché. Mantén los parámetros iguales dentro de cada pareja de una variante.
2. Para cada tarea ejecuta dos pares en orden alterno, primero A/B y luego B/A. Usa sesiones sin contexto previo. Conserva el tiempo hasta el primer resultado de validación, la duración de la sesión, el resultado de la comprobación independiente, y los tokens facturados si la herramienta los ofrece. Registra los fallos y timeouts con su duración.
3. Prepara una fila JSON por ejecución, siguiendo el ejemplo. Un tiempo de validación ausente o los tokens desconocidos se omiten. Conserva los detalles de las pruebas y el diff fuera del fichero de puntuación.
4. Ejecuta el resumen. El script exige parejas completas, calcula diferencias de tiempo en éxitos emparejados y carga total por entrega válida contando sesiones fallidas. Los resultados se agrupan por agente y modelo. El objetivo orientativo de adopción se fija antes de correr el piloto. Verifica los casos críticos antes de añadir al archivo raíz las instrucciones que mejor funcionen.

```json
{"task":"chat-draft","pair":1,"variant":"A","agent":"nombre-y-version","model":"modelo","success":true,"elapsedSeconds":123.4,"firstValidationSeconds":81.2,"billedTokens":8900}
```

```powershell
node scripts/summarize-agent-pilot.mjs .artifacts/test-output/agent-pilot.jsonl
```

El piloto de lectura y localización de archivos puede revelar comandos rotos. Solo los ensayos con un parche y evaluación independiente cuentan para decidir si hay mejora. Si el modelo no lee `AGENTS.md`, anota el mecanismo alternativo y evalúa esa configuración por separado.

