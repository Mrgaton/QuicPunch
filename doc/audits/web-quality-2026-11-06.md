# Calidad de la web: revisión del 6 de noviembre de 2026

## Alcance

Revisión coordinada de seguridad del servidor local, rendimiento del frontend,
accesibilidad, navegación móvil y sincronización de «Ver juntos». Se conservaron
los cambios de desarrollo que ya existían en el árbol de trabajo. Este documento
describe exclusivamente las mejoras y comprobaciones de esta revisión.

## Cambios

| Área | Problema corregido | Evidencia |
| --- | --- | --- |
| API local | Las rutas de ping ejecutaban su manejador antes de comprobar CSRF y tamaño del cuerpo. | HTTP real: token ausente/incorrecto devuelve 403, cuerpo excesivo devuelve 413, ningún efecto lateral; petición autorizada funciona. |
| Archivos estáticos | Resolución demasiado permisiva por sufijo de recurso y rutas de desarrollo sin confinamiento explícito. | Recursos públicos válidos funcionan; recursos ajenos a `QuicNet.wwwroot` y traversal rechazados. |
| Capacidad del servidor | Los sockets de Ver juntos consumían plazas HTTP durante toda su vida; los del panel liberaban su plaza tras conectarse y quedaban sin límite persistente. | Reservas independientes de 32 plazas HTTP y WebSocket; exceso rechazado con 503 y `Retry-After`. Prueba con 32 sockets reales, GET y POST operativos y reutilización de plaza tras cerrar uno. |
| Cierre del servidor | Al cerrar conexiones y disponer la fuente de cancelación, el bucle del listener podía consultar su token demasiado tarde y dejar una excepción sin observar. | El token se captura antes de programar el bucle; verificación conjunta de cierre y admisión en la suite de seguridad. |
| WebSocket de Ver juntos | Eventos simultáneos escribían sin exclusión al mismo socket; un cliente lento podía retener trabajo. | Exclusión por cliente, difusión independiente, máximo de 32 envíos pendientes, plazo de envío de 5 s y cierre de 2 s. Pruebas de orden, saturación, cliente bloqueado y cierre. |
| Conexión del panel | Eventos de un socket antiguo podían afectar al sustituto; cancelar una salida dejaba la página sin reconexión. | Regresiones de sockets antiguos, peticiones pendientes, salida cancelada y restauración de página. |
| Tablas adaptables | Cada mutación de la interfaz provocaba revisar todas las tablas. | En Chromium: una mutación ajena provoca **0 recorridos**; dos inserciones en una tabla durante el mismo frame provocan **1 recorrido**, sin recorrer la otra tabla. |
| Contactos | El editor admitía números truncados como `443abc` y no interpretaba algunos rangos mostrados por la propia interfaz. | Validación estricta, rangos con espacios, deduplicación, errores en español y pruebas del editor. |
| Accesibilidad | Etiquetas del editor sin asociación completa, ayudas sin relación explícita y navegación móvil incompleta. | Seis campos etiquetados, error anunciado, borrador conservado tras 503, reintento con Enter, foco y ruta actual en herramientas móviles. |
| Navegación | La intercepción SPA capturaba el enlace para saltar al contenido. | Enlace de salto con comportamiento nativo y prueba de foco en el contenido. |
| Ver juntos | Una respuesta HTTP tardía podía sustituir una sala más reciente recibida por WebSocket; un sondeo en curso podía absorber un aviso de cambio. | Peticiones compartidas con revisión de estado y actualización posterior a acciones/avisos; pruebas de respuesta y error obsoletos, cambios de medio y reproducción. |
| Invitaciones | Envíos repetidos podían iniciar varias conexiones y no había estado de espera del formulario. | Una operación por formulario, estado ocupado accesible y código conservado cuando falla para poder corregirlo. |

## Validación

- Compilación `Test` de `QuicPunchTests`: sin errores. Persisten seis advertencias
  de nulabilidad en `SecurityDiscoveryTests.cs`, fuera de los cambios de esta revisión.
- **23/23 suites web rápidas**.
- **50 suites de navegador aprobadas**: ejecución general de 48 suites más las
  nuevas `AccessibleToolsBrowserTests` y `TableLabelPerformanceBrowserTests`.
  Las suites Watch se repitieron después del último ajuste de sincronización.
- `DesignAuditBrowserTests` aprobada también de forma aislada tras corregir su
  espera de la respuesta de concesión de permisos, manteniendo la aserción.
- Suites C#: `webui-security`, `watch-reliability`, `peer-acl`, `antireplay` y
  `aot-json`, aprobadas. La suite de seguridad se repitió al ampliar las pruebas
  de concurrencia y cierre.
- Revisión visual de capturas de inicio a 1440 px y Ver juntos a 390 px.
- Revisión cruzada entre agentes de los cambios de estado del frontend y del
  ciclo de vida de los sockets de Ver juntos.

Comandos reproducibles desde la raíz:

```powershell
./scripts/test.ps1 -Tests webui-security,watch-reliability,peer-acl,antireplay,aot-json
node scripts/test-web.mjs --web
node scripts/test-web.mjs --browser --no-build
```

Los registros y capturas regenerables se guardan en `.artifacts/`. No se usaron
claves de publicación ni se generó una publicación firmada.

## Límites y siguientes comprobaciones

Las pruebas de navegador utilizan Chromium y fixtures aisladas. Validan flujos,
errores y estados de la interfaz, pero no sustituyen pruebas de llamadas y
transferencias entre dispositivos reales con WAN/Tor, Android y Linux. La medida
de rendimiento anterior cuenta recorridos DOM; no es una medida de FPS, CPU o
capacidad de red en producción.

Antes de declarar una versión preparada para una implantación empresarial:

1. Verificar los paquetes finales en Windows, Android y Linux, incluidas las
   capacidades nativas de audio, vídeo, archivos y VPN.
2. Ejecutar sesiones largas entre dispositivos con pérdida de red, cambio de
   transporte y revocación de permisos durante llamadas y transferencias.
3. Medir consumo de memoria, CPU y latencia con historiales y directorios grandes,
   y con varias ventanas activas.
4. Completar revisión con lectores de pantalla y navegadores de las plataformas
   soportadas, además de las comprobaciones automáticas de teclado y foco.
5. Resolver las advertencias de nulabilidad restantes y realizar una auditoría
   independiente de seguridad antes de atribuir garantías de producción.

Estas comprobaciones pendientes no se presentan como aprobadas por esta revisión.
