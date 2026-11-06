# Limpieza por fases — 23 de octubre de 2026

## Conservación del trabajo anterior

El repositorio ya tenía numerosos cambios pendientes. No se ha usado reset, checkout ni git clean, ni se han creado commits. La copia de fuentes anterior a esta intervención y su manifiesto SHA-256 están en `backups/cleanup-2026-10-23/sources-before.zip` y `manifest-before.json`. La copia no contiene claves privadas. La WebUI antigua se ha archivado en `backups/cleanup-2026-10-23/legacy-webui`.

Se observaron cambios externos durante la ejecución, especialmente en identidad visual y sus pruebas. Se conservaron y se integraron sus rutas actuales; esta limpieza no revierte ese trabajo.

## Fases implementadas

1. **Base reproducible.** Copia de fuentes, estado inicial de Git y comprobaciones previas. Para contrastar fallos se reconstruyó la versión anterior desde el archivo, sin reemplazar los fuentes de trabajo. Las reproducciones y sus resultados están en la carpeta de backup; su copia temporal de compilación era regenerable y se eliminó al limpiar.
2. **Restos y espacio.** Retirado el helper privado sin llamadas `PeerApiModule.PermissionForProtocol`, los dos archivos MSBuild vacíos de NetLocate y los directorios vacíos `QuicWatch`, `New folder` y `artifacts`. Se comprobó que no había un Directory.Build.props/targets de un padre que los archivos vacíos estuvieran ocultando. Corregida la descripción de BBR: se elige antes del handshake. Se corrigió también la ruta del mensaje de uso del NativeBuilder.
3. **Propiedad de recursos.** `QuicNet.Shared/wwwroot` y `QuicNet.Shared/Watch/wwwroot` contienen la interfaz compartida. `QuicNet.Geolocation/Map` contiene mapa, renderer y licencia, utilizados por la aplicación, Android y NetLocate. Se conservaron nombres de recursos embebidos, URLs HTTP y nombres de licencias publicadas. Pruebas, preview, generación de datos y documentación usan las nuevas rutas. `QuicPunch.Vpn/wintun.dll` es la única copia de origen; MSBuild la propaga transitivamente a salida y publicación. Se verificó que las tres copias originales tenían el mismo SHA-256.
4. **Pruebas de navegador.** Playwright está fijado en package.json/package-lock.json. `BrowserFixture.mjs` centraliza navegador, overrides y arranque/cierre del preview. El preview termina también si desaparece el proceso de prueba. `scripts/test-web.mjs` comparte descubrimiento y diagnóstico entre npm y PowerShell; `-Browser`, `-BrowserFilter`, `--list` y `--no-build` permiten selección explícita. No hay omisiones silenciosas. Los fixtures de vídeo se regeneran cuando hacen falta; FFmpeg admite configuración explícita y detección de WinGet.
5. **Responsabilidades.** El ciclo de vida general, WAN y Tor se agrupan con su estado privado en `QuicPunch.Lifecycle.cs`, `QuicPunch.WanLifecycle.cs` y `QuicPunch.TorLifecycle.cs`, conservando cuerpos de métodos y API. La fachada queda en unas 3.300 líneas. `voice.js` conserva coordinación e interfaz; captura de audio, reproducción PCM y decodificación se instancian mediante tres factories con dependencias explícitas. El coordinador queda en unas 3.800 líneas. Las pruebas VM y los bridges de navegador cargan la misma composición de scripts.

## Mantenimiento de fixtures y defectos encontrados durante la comprobación

- Se actualizaron selectores de pruebas que confundían el nombre con las iniciales de las cápsulas de identidad, o que buscaban indicadores de presencia retirados de esas cápsulas.
- Las pruebas esperan la carga asíncrona de la lista de archivos y la existencia de opciones HTML, sin exigir que una opción de un select cerrado sea visible.
- La prueba de llamadas abre el drawer de contactos antes de pulsar sus controles.
- La prueba de seguridad usa la CSP de la aplicación, no la política más restrictiva del mapa aislado. Chromium recibe permiso de loopback para las páginas interceptadas de prueba; no se relaja la CSP de producción ni se concede permiso de micrófono/cámara.
- La prueba OfflineAudioContext inicia el productor sintético mediante suspensiones del reloj de audio después de arrancar el worklet, evitando perder el primer lote durante su construcción. Conserva los umbrales de amplitud, continuidad, silencio final y limpieza.
- Se unificó la altura móvil reservada para navegación y la barra real en `--mobile-nav-height`. Antes, a 320 px, la barra podía medir 78 px y el contenido reservaba 76 px, solapando chat y QuicWatch.
- El script de limpieza seleccionaba la sobrecarga Int32 de Math.Max y fallaba al contabilizar carpetas mayores de 2 GiB después de borrarlas. Usa ahora operandos Int64; se comprobó el cálculo con 3 GiB.

## Limpieza del disco

La primera ejecución seleccionó 20.646,57 MiB. Eliminó la carpeta `.artifacts` de 10.732,65 MiB y contabilizó otros 9.791,09 MiB: aproximadamente **20,0 GiB** retirados. El error Int32 descrito arriba impedía sumar correctamente la primera cantidad. La segunda ejecución utilizó el cálculo corregido.

Algunos archivos de `QuicNet/bin/Debug` estaban bloqueados por la aplicación abierta. Se conservaron; no se cerró la aplicación ni se forzó el cierre de procesos del usuario. Se preservaron backups, recursos nativos y datos de usuario. La verificación posterior regeneró sus bin/obj y algunos resultados temporales.

## Validación y límites

Los comandos, resultados finales y publicaciones locales están registrados en `backups/cleanup-2026-10-23/`. Los logs detallados de cada suite están en `.artifacts/test-output/`; la evidencia anterior a la limpieza se conservó en `verification-before-clean.zip`.

La batería inicial de 21 suites C# pasó en 19 casos: permisos, conectividad, WebUI, audio, Opus, datagramas, multiplexer, recuperación de sesiones, cancelación, recuperación de conexiones, QuicWatch, geolocalización, actualizaciones y VPN. Se investigaron aparte los dos fallos restantes:

| Comprobación previa | Reproducción con los fuentes anteriores |
| --- | --- |
| `--test-automatic-transport` | Mismo timeout: el contacto guardado no se reconecta después del cierre remoto por inactividad. |
| `--test-security` | Misma comprobación de actualización Nostr de IP/puerto: un puerto distinto sustituye al valor esperado en PeerStore. |
| Compilación de NetLocate | Mismos 14 errores: su selección de fuentes compartidos no incluye tipos que el motor actual necesita, entre ellos ProbeBudget, FingerprintDatabase, LocalityAssessment y EnhancedEvidence. |

Estos problemas no se han ocultado, saltado ni corregido cambiando algoritmos de red o ampliando el motor incluido en NetLocate: requieren cambios funcionales separados. Los binarios y recursos de escritorio/geolocalización se verifican por separado. Android no se compila en este equipo porque no hay un workload Android instalado; se revisaron sus referencias y recursos, pero eso no sustituye una compilación ni una prueba en dispositivo. Tampoco se certifica una conexión WAN/Tor real o una sesión con dispositivos de captura físicos.

### Resultado integrado final

| Verificación | Resultado |
| --- | --- |
| Reconstrucción del harness tras limpiar bin/obj | Correcta; seis advertencias nullable en SecurityDiscoveryTests, sin errores. |
| C# final: output, sha3, antireplay, webui-security, geolocation-workbench, datagram-categorization, opus, native-audio, watch-reliability, pqc-handshake | **10/10 suites**. |
| Pruebas web rápidas | **19/19 suites**. |
| Pruebas de navegador, sin filtros ni omisiones | **42/42 suites**. |
| Sintaxis de los scripts y fixtures JavaScript | Correcta. |
| Filtro PowerShell combinado con -Web y -NoBuild | Verificado aparte: mantiene las 19 suites rápidas y selecciona las suites Voice de navegador. |
| Publicación Windows Native AOT y arranque --version | Correctos; `AOT=True`. Persisten advertencias de análisis AOT/trimming en código existente. |
| Smoke del AOT copiado a una carpeta aislada, sin acceso a los fuentes por el fallback de assets | Correcto: QUIC, SQLite, identidad, preferencias, APIs, WebSocket, AVIF y **todos los scripts referenciados por el HTML**. |
| Publicación del host de geolocalización | Correcta; arranque autenticado, renderer `/map.js` y mapa embebido con 242 países comprobados mediante HTTP. |
| Wintun publicado | SHA-256 idéntico al único original en QuicPunch.Vpn. |
| Self-test web de geolocalización | 23 comprobaciones correctas con transporte sintético, sin sondeos a Internet. |

La ejecución integrada está en `final-tests.log`; las publicaciones en `aot-publish-final.log` y `geolocation-publish.log`; las comprobaciones aisladas en `aot-isolated-smoke.log` y `map-publish-smoke.log`, dentro del backup indicado arriba. Las dos suites C# y NetLocate que fallaban antes siguen siendo trabajo pendiente, no una batería global satisfactoria.
