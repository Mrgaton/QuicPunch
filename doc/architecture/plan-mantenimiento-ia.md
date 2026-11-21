# Plan para acelerar el desarrollo asistido por IA en QuicNet

Propuesta de implementación · 21 de noviembre de 2026

[Resultados de implementación y validación](resultados-mantenimiento-ia.md)

## Resultado esperado

Un agente con una tarea concreta debe poder localizar el punto de entrada, identificar qué estado y contratos toca, ejecutar la prueba adecuada y entregar un cambio verificable con poca exploración. El objetivo abarca interfaz, APIs, red, multimedia, hosts y publicación. La estructura de proyectos actual sirve de base. Los cambios estructurales se entregan por función y con pruebas comparables.

La auditoría del árbol local de `terror` encontró tres oportunidades inmediatas. El ejecutable C# mantiene por separado el registro de pruebas y la ayuda. La CI ejecuta C# y JavaScript rápido, con Playwright disponible localmente para los flujos de interfaz. El estado central de la WebUI concentra reglas de presentación, acceso a servicios y serialización, y repite parte del trabajo y del JSON. También hay una diferencia importante entre el commit de partida y los cambios locales de la auditoría. Registra el estado exacto antes de usar sus resultados como referencia.

Referencias locales · [Ejecutable de pruebas](../../QuicPunchTests/Program.cs) · [CI](../../.github/workflows/native-msquic-pqc.yml) · [Estado WebUI](../../QuicNet.Shared/WebUi/Modules/StatusApiModule.cs) · [Propuesta de AGENTS.md](plantilla-agents.md)

## Qué respalda la evidencia publicada

- Lulla y colaboradores observaron, en 124 PR de 10 repositorios, un tiempo mediano un **28,64 % menor** y un **16,58 % menos de tokens de salida** con los archivos incluidos en su experimento. Revisaron una muestra de entregas de forma manual. La verificación funcional completa quedó fuera de su estudio. [Artículo, versión 2](https://arxiv.org/abs/2601.20404v2)
- Gloaguen y colaboradores encontraron que los archivos de contexto, tanto generados como escritos por desarrolladores, generalmente no mejoraron la tasa de éxito y aumentaron el coste medio de inferencia más de un **20 %**. Las instrucciones sobre convenciones particulares sí se seguían. Los resúmenes generales del repositorio aportaron poco en su evaluación. [Artículo, versión 3](https://arxiv.org/abs/2602.11988v3)
- Khatri comparó dos agentes en 17 tareas reales y 288 ejecuciones. El estudio no detectó una variación medible en la corrección atribuible a la estrategia de contexto dentro de sus márgenes de equivalencia. [Estudio de ablación](https://arxiv.org/abs/2607.27250v1)

La propuesta reduce el contexto inicial a comandos, convenciones particulares y comprobaciones que el código por sí solo no deja claras. Los detalles van en documentación consultable por tarea. Las mejoras para QuicNet se deben demostrar con una comparación local. Los porcentajes de los estudios anteriores describen sus experimentos.

## Orden de implementación

| Hito | Entrega | Depende de | Esfuerzo relativo |
| --- | --- | --- | --- |
| 0 | Línea base reproducible y corrección de una prueba de interfaz | Estado de partida identificable | Bajo |
| 1 | Comandos de validación, CI y mapa de flujos | 0 | Medio |
| 2 | Evaluación del contexto y de los comandos nuevos | 0 para el contexto, 1 para los comandos | Medio |
| 3 | Primer refactor de producción, el estado de la WebUI | 1 | Medio |
| 4 | Límites explícitos para interfaz, chat, voz y hosts | 1, y 3 si cambia el mismo contrato | Medio, por entrega |
| 5 | Extracciones del núcleo y compilación nativa | 1 y pruebas de la función afectada | Alto, por entrega |

### 0. Fijar el punto de partida

1. Guarda el identificador del commit, la lista de cambios locales, las versiones reales de SDK, Node y Playwright, y los comandos de la auditoría. Elige una base versionada que incluya los cambios locales relevantes. Un `git worktree` creado desde `HEAD` no los incorpora. Trabaja en un entorno aislado y conserva los cambios del usuario en su árbol actual.
2. Reproduce el fallo de `ConnectionModesBrowserTests`. Abre el desplegable «Filtros y conexión» antes de interactuar con `#contactSort`, como ya hace `ContactHomeBrowserTests`. Mantén las demás comprobaciones y registra el resultado de ambas suites.
3. Registra tiempos de compilación inicial e incremental, de Node y de las suites Chromium elegidas. Distingue caché fría de caché caliente y registra fallos preexistentes por separado. Usa `.artifacts/test-output/` para los logs regenerables y conserva los resultados resumidos con la revisión a la que pertenecen.

Comandos disponibles en el punto de partida, desde la raíz y después de instalar las dependencias del entorno.

```powershell
dotnet --version
node --version
./scripts/test.ps1 -Tests output,sha3,connection-modes -Web
node scripts/test-web.mjs --browser --list
node scripts/test-web.mjs --browser --filter=ContactHome
node scripts/test-web.mjs --browser --filter=ConnectionModes
```

**Aceptación.** La base se identifica y se puede reconstruir. Las dos pruebas de contactos pasan en el Chromium de Playwright. El informe registra tiempos y cualquier suite que falle antes de los cambios de producción.

Referencias · [Prueba de modos](../../QuicPunchTests/Tests/ConnectionModesBrowserTests.mjs) · [Prueba de contactos](../../QuicPunchTests/Tests/ContactHomeBrowserTests.mjs) · [SDK](../../global.json) · [Dependencias de Chromium](../../package.json)

### 1. Dar una ruta rápida y confiable para editar y validar

- **H1a. Registro de pruebas.** En `QuicPunchTests/Program.cs`, usa una sola definición para nombre, alias, función, descripción y requisitos que necesite el ejecutable. A partir de ella, implementa `--list` y genera `--help`. Conserva los flags actuales, los códigos de salida, la preparación de las pruebas sin reflexión JSON, las restricciones de `--loopback-only` y las entradas especiales como `--serve-webui-preview`. Verifica el descubrimiento, un alias y el error de un nombre inválido. Conserva `scripts/test.ps1` como entrada para quien ya lo usa.
- **H1b. Validación y compilación.** Define una comprobación rápida con JS, una por área y una de integración con requisitos explícitos. Haz que la opción `--no-build` de Node solo se anuncie si tiene un efecto verificable. En el código auditado se interpreta, pero no se usa tras leerla. Reutiliza el runner y sus fixtures. Mantén el resumen por suite, los códigos de salida y el acceso al log. Mide qué tareas realmente necesitan compilar antes de separar o cachear builds.
- **H1c. CI de interfaz.** Añade un trabajo de Chromium que arranque con `npm ci` y Chromium de Playwright. En cada cambio relevante ejecuta un conjunto inicial como `ContactHome`, `ConnectionModes`, `ChatOutbox` y `VoiceRoom`. Incluye en el disparador los recursos web, las pruebas, los fixtures, el preview, los scripts y el lockfile. Conserva trazas o logs si falla. Mantén la batería completa disponible bajo demanda y amplía la selección cuando los datos de estabilidad y duración lo justifiquen.
- **H1d. Mapa de flujos.** Incorpora a la documentación operativa una tabla con entrada, propietario del estado, contrato y prueba. Empieza por las rutas de la tabla siguiente. Enlaza el mapa desde `doc/README.md` y evita duplicar catálogos de tests en varias guías. Valida en CI los enlaces locales del mapa y los nombres de suites contra el registro. Corrige primero las referencias activas que apunten a rutas antiguas.

| Cambio | Dónde empezar | Validación existente |
| --- | --- | --- |
| Diseño, accesibilidad o navegación | `QuicNet.Shared/wwwroot/index.html`, la vista y su CSS. Sigue el JS que abre la vista | Playwright con el filtro del flujo, más `npm run test:web` si cambia JS |
| Chat | `chat.js`, `ChatApiModule.cs`, `ChatHandler.cs` y el historial si toca persistencia | Playwright `ChatIsolation` o `ChatOutbox` |
| Voz y pantalla | `voice.js`, `VoiceApiModule.cs`, `VoiceCallHandler*.cs`, `Video/` si cambia captura o codificación | Playwright `VoiceRoom` o `ScreenViewer` y C# `voice-mobility` o `screenshare` |
| Estado y APIs WebUI | `WebUiServer.cs`, el módulo de API, `JsonContracts.*` y el consumidor JS | C# `webui-security`, `aot-json` y prueba del consumidor afectado |
| Conectividad y transporte | `QuicPunch.Lifecycle.cs`, `Discovery/`, `QuicPunch.PeerTransport.cs` y `Multiplexer/`, según flujo | C# `peer-state`, `connection-recovery`, `session-recovery` o `multiplexer` |
| Adaptadores de escritorio o Android | `QuicNet/Program.cs` o `QuicPunch.AndroidProbe/AndroidConnectionService.cs`, más el adaptador concreto | Build del host y prueba del flujo. Para el WebView, comprueba la plataforma afectada |
| VPN, QuicWatch o actualizador | `QuicPunch.Vpn/`, `QuicNet.Shared/Watch/` o `QuicNet.Shared/Updates/` | C# `vpn-policy`, `watch-reliability` o `updates`, respectivamente |
| Publicación o código nativo | `Release.props`, `scripts/`, `QuicPunch.NativeBuilder/` y el build del host | Prueba aislada de arranque y empaquetado, más verificación de la plataforma y del componente afectado |

**Aceptación.** La ayuda y la lista del ejecutable salen del mismo registro. Un cambio web típico tiene una comprobación rápida que funciona sin compilación .NET cuando su fixture lo permite. Las suites seleccionadas de Chromium corren en CI. El mapa enlaza a rutas comprobadas y el runner conserva los diagnósticos y modos existentes.

Referencias · [Runner C# y web](../../scripts/test.ps1) · [Runner Node](../../scripts/test-web.mjs) · [Guía de pruebas](../testing/test-suite.md)

### 2. Comprobar si las instrucciones ahorran trabajo

**Experimento del archivo.** A usa el `AGENTS.md` vigente. B usa la propuesta, que conserva las reglas vigentes. Mantén igual el resto del árbol, incluida la documentación, y usa el mismo enunciado de tarea, agente, modelo, herramientas, permisos, límite y equipo. Alterna el orden de A y B. Usa sesiones nuevas y un estado equivalente del checkout y de la caché en cada par. Si alguna herramienta no lee `AGENTS.md`, registra cómo recibió las instrucciones y analiza sus resultados por separado.

**Experimento de herramientas.** Repite tareas equivalentes con las mismas instrucciones sobre la base y sobre una variante que añade únicamente el mapa y las mejoras del runner. Si se mide CI o se cambian los comandos disponibles, registra ese factor como parte de la variante. Evalúa los refactors de producción con tareas nuevas de la función afectada y las mismas comprobaciones de comportamiento. Conserva una comparación por factor cuando el coste lo permita.

- Usa ocho tareas representativas con condiciones de éxito preparadas antes de iniciar. Ejemplos para seleccionar y concretar en el checkout son un ajuste de navegación móvil, un caso de borrador de chat, un cambio de contrato JSON, un cambio de estado de voz, una regresión de reconexión, una diferencia de un adaptador, un caso del actualizador y un ajuste del runner.
- Ejecuta dos pares por tarea con el primer agente, 32 ejecuciones para A/B. Conserva casos de dificultad variada. Si el resultado es prometedor, repite con otro agente o modelo y amplía las tareas si los resultados se contradicen. Un piloto pequeño da una señal de dirección.
- Evalúa cada parche con pruebas objetivo fijadas de antemano y una revisión breve del diff. Cuenta como éxito solo el cambio que cumple los requisitos, pasa las pruebas aplicables y mantiene los contratos del caso. Mantén las comprobaciones de evaluación fuera del contexto inicial del agente, salvo las que formen parte del encargo normal.
- Registra éxito, tiempo hasta el primer resultado de validación, tiempo total, compilaciones, herramientas, entradas y salidas de tokens si están disponibles y coste observado. Calcula el coste y el tiempo por entrega válida incluyendo el esfuerzo de ejecuciones fallidas. Presenta resultados por área y modelo, con medianas de diferencias por tarea y la dispersión. Registra el estado de caché y cualquier timeout.
- **Objetivo de adopción propuesto.** Elige la métrica principal antes de ejecutar. Mantén la tasa de tareas válidas sin perder casos críticos y busca reducir al menos un 15 % la mediana del tiempo de las entregas válidas o los tokens facturados por entrega válida. Evita una degradación superior al 10 % en la otra métrica cuando esté disponible. Trata los porcentajes como objetivos operativos y comprueba que la mejora se repita en varias tareas y una segunda configuración antes de anunciarla como confirmada. Si el resultado es mixto, incorpora solo las instrucciones útiles y vuelve a medir.

**Entregable.** Un resumen reproducible con base, variantes, tareas, comandos del evaluador, resultados y decisión. Los límites de tiempo y el coste de modelos se acuerdan al preparar la ejecución del experimento.

### 3. Simplificar y medir el estado de la WebUI

1. **Caracteriza el contrato.** Identifica usuarios de `/api/status`, `status.get` y `status_updated`, incluyendo preview, JavaScript y scripts de diagnóstico. Fija en pruebas la forma actual y los tipos JSON para un conjunto pequeño de contactos, datos ausentes y opciones de chat/portapapeles. Comprueba serialización con los contextos utilizados por la aplicación.
2. **Extrae por responsabilidad.** Separa la proyección de contactos del ensamblado del estado de la aplicación. Introduce nombres de tipos que describan el contrato. Mantén las rutas, los nombres de campos, los valores nulos y los alias consumidos. Conserva la agrupación y el envío de eventos en el hub WebSocket.
3. **Mide y elimina trabajo redundante.** Usa conjuntos sintéticos de 0, 50 y 500 contactos en el mismo equipo y configuración. Mide proyección y serialización tras calentar, con varias repeticiones. Registra p50/p95, bytes asignados por operación y tamaño de mensaje. Mide HTTP y WebSocket con uno y varios clientes si el cambio afecta el envío. Empieza por reutilizar una lectura de interfaces por snapshot y usar índices de la propia proyección donde lo justifique el perfil.
4. **Evoluciona el JSON en una entrega aparte** si duplicar campos tiene un coste relevante. Migra primero los consumidores que identifiques y conserva compatibilidad durante la transición, o define una versión de contrato cuando se necesite.

**Aceptación.** HTTP y WebSocket conservan los contratos comprobados y pasan las pruebas de seguridad, de serialización y de los consumidores. El informe cuantifica el efecto sobre tiempo, asignaciones y bytes por escenario. Si el beneficio es de legibilidad, descríbelo como tal.

Referencias · [Proyección actual](../../QuicNet.Shared/WebUi/Modules/StatusApiModule.cs) · [Hub WebSocket](../../QuicNet.Shared/WebUi/WebUiWebSocketHub.cs) · [Consumidor web](../../QuicNet.Shared/wwwroot/app.js)

### 4. Mejorar interfaz, funcionalidad y plataformas por separado

- **Interfaz.** Empieza los cambios visuales localizando la vista y las reglas CSS efectivas. Usa los recursos reales de la interfaz con el preview y comprueba el host si cambia el contrato. Incluye casos de 320–390 px y escritorio cuando afecte navegación, secciones o contenido adaptable. Comprueba el recorrido con teclado, el foco y los estados vacío, desconectado y de error cuando apliquen. Para cambios visuales, adjunta capturas del viewport afectado y comprueba Android WebView cuando el cambio dependa del motor o del comportamiento táctil.
- **Chat.** Extrae los borradores como primera unidad de estado, con un cliente HTTP y un reloj o programador inyectables si se necesita probar el guardado diferido. Mantén la integración de la vista. Comprueba guardado pendiente al cambiar de contacto y recarga sin mezclar borradores. Usa las pruebas actuales de aislamiento y envío como comprobación de integración.
- **Voz y multimedia.** Usa el patrón de las fábricas existentes de audio de pantalla para extraer una responsabilidad de `voice.js`. Elige por frecuencia de cambios, por ejemplo selección de fuente o estado del visor. Prueba desuscripción al cerrar, reconexión y estados de permiso. Registra latencia, cuadros descartados y consumo si cambia el procesamiento de vídeo. Usa primero fixtures sintéticos.
- **Hosts.** Compara qué partes del registro de protocolos y montaje de QuicWatch coinciden en escritorio y Android. Extrae solo composición compartida que aparezca en una misma secuencia, con propiedad y orden de cierre explícitos. Verifica permisos, segundo plano, recuperación y disponibilidad por plataforma.

**Aceptación.** Cada entrega recorre una función de punta a punta y puede revertirse de forma independiente. Los módulos extraídos tienen un propietario claro del estado. Las pruebas comprueban los flujos existentes y las transiciones que motivaron la extracción.

Referencias · [Chat](../../QuicNet.Shared/wwwroot/chat.js) · [Voz](../../QuicNet.Shared/wwwroot/voice.js) · [Escritorio](../../QuicNet/Program.cs) · [Android](../../QuicPunch.AndroidProbe/AndroidConnectionService.cs)

### 5. Acotar el núcleo y el ciclo de compilación

- Empieza con el registro de pares descubiertos bajo la fachada `QuicPunch`. Identifica todas las mutaciones del índice y el inventario, la identidad que los enlaza y las reglas de expulsión. Antes de extraer, comprueba admisión, expiración, acceso concurrente y conservación de los pares protegidos. Mantén el comportamiento observable de la API y pasa las suites de estado, recuperación y multiplexación.
- Deja para entregas posteriores la negociación y la recuperación de sesiones. Cuando cambien esas rutas, verifica cancelación, cierre y reinicio, intentos concurrentes, autorización por protocolo e identidades WAN/Tor según el caso. Compara el rendimiento con el benchmark QUIC del repositorio en procesos frescos cuando se toque una ruta de datos.
- Mide el builder nativo por separado. El objetivo MSBuild puede generar binarios bajo `QuicPunch/runtimes/`. Si ralentiza el flujo, valida primero qué trabajo se repite y extrae la preparación cacheable de los pasos que forman parte de una publicación. Verifica invalidación al cambiar el RID, los fuentes, los parches o el lockfile. Comprueba que una compilación incremental gestionada no sobrescribe binarios versionados innecesariamente y que el empaquetado conserva los activos requeridos y la prueba PQC.

**Aceptación.** Cada extracción preserva las invariantes verificadas. Cualquier cambio de build se demuestra en una compilación limpia, otra incremental y una publicación local de prueba para cada RID afectado que se pueda ejecutar. Registra por separado las plataformas o escenarios pendientes.

Referencias · [Fachada](../../QuicPunch/QuicPunch.cs) · [Ciclo de vida](../../QuicPunch/QuicPunch.Lifecycle.cs) · [Build nativo](../../QuicPunch/QuicPunch.csproj) · [Benchmark](../../QuicPunchTests/Tests/QuicPerformanceTests.cs)

## Encargo inicial listo para pasar a otro agente

> Implementa el hito 0 y después H1a–H1b, en entregas revisables. Trabaja desde una base que represente el árbol que se desea mejorar y conserva los cambios del usuario. Reproduce y corrige el bloqueo en `ConnectionModesBrowserTests` abriendo el desplegable como lo haría un usuario. Luego unifica el registro y la ayuda de C#, añade `--list` y verifica que los modos, alias y códigos de salida existentes funcionan. Comprueba el comportamiento real de `--no-build` en el runner de Node y alinea implementación y ayuda según el uso de los fixtures. Usa las pruebas existentes y añade cobertura donde el registro nuevo pueda romper un contrato. Entrega el diff, comandos, resultados, medición inicial y cualquier fallo preexistente identificado. Sigue con H1c y H1d cuando las comprobaciones del primer bloque sean reproducibles.

Para encargos posteriores usa cuatro datos en el mensaje inicial. Describe el comportamiento esperado, el flujo o archivo de entrada si se conoce, los contratos que deben mantenerse y la prueba que demostrará la entrega. Guarda las reglas estables en el archivo raíz y las referencias específicas en la documentación del flujo afectado.
