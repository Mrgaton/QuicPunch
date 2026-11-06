# Ampliación regional y límites de la escala de ciudad

Experimento local del 18 de octubre de 2026. No se ha suministrado una posición física real del equipo; no hay una evaluación del error geográfico ni cobertura estadística calibrada.

## Datos incorporados

El ejecutable incluye 9005 endpoints: los 1992 anchors anteriores y 7013 destinos adicionales de 5453 sondas públicas conectadas de RIPE Atlas, en 133 países. Los adicionales incluyen 2451 IPv4 y 4562 IPv6; en España son 26 IPv4 y 38 IPv6. Las direcciones se deduplican y las dos familias de un emplazamiento comparten SiteId.

Se descargaron metadatos públicos durante la preparación del dataset, no durante las estimaciones. El snapshot filtra conectividad y estabilidad de 30 días, disputas de ubicación, sondas en espera, NAT IPv4 conocido y etiquetas de VPN, túnel o anycast. Los filtros no prueban ausencia de túneles, corrección de las coordenadas o respuesta ICMP entrante. El campo de estabilidad describe la conectividad saliente de Atlas.

Cada destino adicional conserva locationVerified=false y coordinateEvidence=operator-declared. Sus coordenadas las declara el propietario; no se convierten en una posición certificada por responder a un ping. La incertidumbre nominal de 50 km tampoco es cobertura estadística ni cubre necesariamente cualquier declaración errónea.

Fuentes y hashes por página: Data/regional-manifest.json. Recurso comprimido: Data/regional-probes.json.gz, SHA-256 D9D9C33D62567AFA60F046675E83A1259809FC56F61D444EC21D451678B5D6BB. El ejecutable verifica ese hash al cargarlo. tools/Build-RegionalCatalog.ps1 permite regenerar los datos durante desarrollo y deja la caché en obj/.

La API documenta que la ubicación es suministrada por el propietario y que la IP puede no estar disponible: [referencia de probes](https://atlas.ripe.net/docs/apis/rest-api-reference/probes/probes_list/). Las coordenadas públicas también llevan ofuscación de privacidad: [acceso y privacidad](https://atlas.ripe.net/docs/apis/rest-api-manual/authentication/anonymous-access/). Los tags y sus límites están documentados por [RIPE Atlas](https://atlas.ripe.net/docs/getting-started/probe-tags/). Las condiciones del dataset se conservan en NOTICE.txt, separadas de la licencia del código.

## Qué cambia en la selección y en el reloj

regional-v3.1 conserva cobertura global y reserva referencias principales. Las señales rápidas, incluidas las declaradas, orientan nuevas sondas; dos huecos buscan áreas cercanas aún no medidas para no agotar una ciudad densa. Se conservan diversidad de emplazamiento y ASN, posibles recortes geométricos y revisión de una referencia útil. Un ASN solo guía sondeos: no implica ubicación ni independencia de rutas.

La región principal sigue utilizando únicamente las referencias principales. Otra región, expresamente condicional, incorpora las posiciones declaradas con su margen nominal. Se usan todas las declaraciones medidas: si son incompatibles, se retira la hipótesis y el color; no se quitan las incómodas para obtener un resultado agradable. El mapa, métricas y leyendas separan ambas regiones.

El transporte LIVE nuevo mide con Stopwatch el intervalo completo que contiene envío y respuesta, incluyendo sobrecoste de llamadas, scheduling y recepción. Usa el máximo de ese intervalo y el RTT que devuelve el sistema. No resta una latencia de acceso supuesta. Si Stopwatch tiene alta resolución, guarda TimingMethod=elapsed-upper-v1. El estimador conserva un margen nominal de reloj de al menos 0,05 ms; si el modelo añade margen por encima del milisegundo original, también conserva ese exceso. No se considera una calibración individual del equipo.

Los registros antiguos siguen usando la cuantización entera y el margen original. La elección de muestra utiliza RTT más el margen de su método, para no preferir un cero redondeado frente a una medida fraccional más informativa. La velocidad sigue siendo 299,792458 km/ms; no se usa velocidad de fibra ni se resta acceso para forzar un resultado menor.

## Mediciones reales

Ambas ejecuciones nuevas emplearon 30 rondas × 12 destinos × 3 sondas, 1080 intentos, máximo 20 paquetes/s, timeout 600 ms y presupuesto 180 s. No se reutilizaron muestras ni ubicaciones estimadas de una sesión anterior para construir las cotas. Los tiempos siguientes son el intervalo entre el inicio del primer y último endpoint medidos; no incluyen todo el tiempo de escritura del informe.

| Ejecución | Intervalo de medición | Región principal km² / radio | Hipótesis declarada km² / radio | RTT declarado mínimo |
|---|---:|---:|---:|---:|
| regional-v3 inicial | 94,38 s | 6 687 667 / 1721 km | 308 648 / 345 km | 1,5379 ms |
| regional-v3.1 | 96,86 s | 6 537 866 / 1697 km | 299 094 / 342 km | 1,5010 ms |

En la segunda ejecución respondieron 303 destinos: 158 principales y 145 declarados. Las declaraciones que respondieron abarcan 141 ASN; no se atribuye independencia estadística por ese motivo. Se encontró una señal inferior a 2 ms en la ronda 20, frente a la ronda 21 del prototipo; esa diferencia aislada no es una garantía de mejora.

El registro anterior, con solo anchors y reloj entero, había terminado en 6 331 029 km² y radio 1693 km. **La región principal no mejoró frente a aquella sesión.** El avance nuevo está en localizar destinos de latencia mucho menor y disponer de una hipótesis condicional más acotada, sin presentar esa hipótesis como precisión confirmada. Tampoco se trata de un ensayo A/B con condiciones de red idénticas: rutas y congestión varían y cambió el método de reloj.

Varias respuestas rápidas corresponden a coordenadas declaradas en Barcelona. Esto identifica señales de infraestructura, no demuestra que el equipo esté dentro de ese municipio. Incluso 1,501 ms, con margen de reloj de 0,05 ms y 50 km nominales de incertidumbre del landmark, produce una cota individual de unos 282,5 km. Intersecciones de otras cotas pueden reducirla; no existe una justificación para convertir automáticamente esa señal en un radio de ciudad.

Grabaciones originales y resultados: measurements/city-study/regional-v3-first/ y measurements/city-study/regional-v3.1/. Cada JSONL congela catálogo, procedencia, método de reloj, modelo, selección y muestras. Los antiguos JSONL únicos se han copiado también a measurements/previous-snapshots/, fuera de carpetas de compilación o limpieza.

## Refinamiento local y reproducción

REFINE-GRID conserva muestras, cotas, procedencia y parámetros físicos; cambia solo discretización y presupuesto de celdas. Genera un nuevo JSONL con el SHA-256 del original y permite reproducir el derivado sin red. No presenta píxeles de 5 km como error físico de 5 km. Si alcanza el presupuesto de celdas, mantiene la aproximación conservadora más amplia.

Sobre el segundo registro, el refinamiento sin nuevas sondas produjo **6 287 095 km² / radio 1646 km en la región principal** y **256 473 km² / radio 290 km en la hipótesis declarada**. El radio máximo de celda de esta última fue 3,2577 km, sin que eso implique error físico de ese tamaño. Las cotas individuales y las muestras permanecen idénticas.

El presupuesto de refinamiento se limita a 40 000 celdas para acotar cálculo y tamaño del historial. La escritura comprueba los límites del lector (16 MiB por línea y 256 MiB por registro). El registro derivado validado ocupa 91 623 549 bytes y está en measurements/city-study/regional-v3.1-grid5/. La primera salida de desarrollo, regional-v3.1-refined/, superó el límite y está marcada como descartada; no debe utilizarse como entregable.

~~~powershell
.\dist\win-x64\NetLocate.exe live --planner regional --rounds 30 --samples 3 --pps 20 --timeout-ms 600 --seconds 180 --out .\medicion-nueva
.\dist\win-x64\NetLocate.exe refine-grid .\medicion-nueva\measurements.jsonl --resolution-km 5 --out .\medicion-malla-fina
.\dist\win-x64\NetLocate.exe replay .\medicion-malla-fina\measurements.jsonl --out .\replay
.\dist\win-x64\NetLocate.exe analyze .\medicion-malla-fina\measurements.jsonl --out .\audit.json
~~~

## Validación y conclusión empírica

Las pruebas cubren la separación de declaraciones, contradicciones, vinculación a IP literal, cronometraje nuevo frente al antiguo, caso sintético submilisegundo, serialización AOT, reserva de anchors y refinamiento sin modificar cotas o muestras. La aplicación original conserva sus 116 comprobaciones sin sondeos a Internet.

Se superaron 52 comprobaciones desde el ejecutable Native AOT Windows x64, compilado con SDK 10.0.402 y runtime 10.0.13, sin cabecera CLR. El replay del original es idéntico byte a byte (result.json SHA-256 82AAC25D5F5704FF905664AF54C9790FD3914A4144EB9244132024CBD534EACA); también el del derivado (1218BCCBF2477131F93FFB5B6DAD7E0D9B343F4ED5EB9BB46BA95AE6A832C3DB). La versión 1 anterior conservó su resultado original, sin cambiar sus márgenes.

Chromium verificó mapas, pestañas de rondas, región condicional separada, SVG, vistas 1440×1100 y 390×844 sin desbordamiento horizontal, cero errores y cero solicitudes externas. Node/Playwright son herramientas auxiliares de QA y no se distribuyen. Capturas regenerables en bin/visual-qa/; evidencia del binario y SHA-256 actual en bin/updates/build-evidence.json.

El caso sintético submilisegundo admite un radio inferior a 100 km y conserva la posición de la fixture. No constituye evidencia de ese rendimiento en esta conexión real. **Las mediciones reales todavía no sostienen una localización a escala de ciudad.** Hacen falta referencias más fiables y evidencia de red que delimite más la posición; estrechar el color, suponer una velocidad menor o restar un acceso desconocido no resuelve ese límite.
