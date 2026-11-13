# NetLocate

Ejecutable autónomo **C# / .NET 10 / Native AOT**, Windows x64. Reutiliza los fuentes de QuicNet.Geolocation sin depender de su host web .NET 11. Incluye catálogo, mapa, índice ASN local y licencias; no necesita runtime ni servidor local.

## Ejecutar

Desde esta carpeta:

~~~powershell
.\dist\win-x64\NetLocate.exe live --out .\mi-medicion
.\dist\win-x64\NetLocate.exe replay .\mi-medicion\measurements.jsonl --out .\replay
.\dist\win-x64\NetLocate.exe analyze .\mi-medicion\measurements.jsonl --out .\audit.json
.\dist\win-x64\NetLocate.exe refine-grid .\mi-medicion\measurements.jsonl --out .\malla-fina --resolution-km 5
.\dist\win-x64\NetLocate.exe demo --out .\demostracion
.\dist\win-x64\NetLocate.exe version
~~~

Abre report.html en el navegador. LIVE actualiza el archivo por ronda y el navegador lo recarga cada tres segundos; al terminar cesa la recarga. Las pestañas comparan área, radio, cotas útiles y selección. El informe no carga recursos externos ni ejecuta JavaScript.

Cada LIVE/DEMO necesita un registro nuevo: no sobrescribe measurements.jsonl. Ctrl+C conserva las rondas y endpoints completados. Las muestras de un endpoint interrumpido a mitad de tanda pueden no estar guardadas; el contador incluye todos los intentos enviados.

| Opción LIVE | Valor inicial / límites |
|---|---|
| --catalog landmarks.json | Catálogo local opcional, formato Catalog |
| --planner | regional; acepta information y legacy para comparar |
| --rounds | 30; 1..30 |
| --batch | 12; 6..64 destinos por ronda |
| --samples | 3; 1..20 por destino |
| --seconds | 180; 1..1800 segundos de presupuesto |
| --parallel | 4; 1..16 endpoints en vuelo |
| --timeout-ms | 1000; 100..10000 |
| --pps | 10; 1..20 paquetes/segundo, limitador compartido |
| --resolution-km | 40; objetivo de discretización, no precisión obtenida |
| --ipv6 | Familia IPv6; por defecto IPv4 |

LIVE envía **ICMP exclusivamente a IP literales del catálogo seleccionado**. No descubre la IP pública, resuelve DNS, descarga datos ni consulta APIs HTTP/geolocalización/ASN remotas. La diversidad ASN se obtiene del índice binario incorporado. No ejecuta procesos auxiliares. El análisis de routers de la aplicación original no se añade automáticamente a estas cotas.

REPLAY reconstruye todas las rondas desde el catálogo, modelo y muestras congelados. No crea sondas ni sustituye datos por los actuales. DEMO usa latencias sintéticas y direcciones de documentación; tampoco envía paquetes. ANALYZE es completamente local.

REFINE-GRID crea un registro derivado sin red: mantiene muestras, relojes y cotas físicas y cambia únicamente el objetivo de discretización y el presupuesto de celdas (40 000, para acotar cálculo y tamaño del historial). Registra el SHA-256 del JSONL original y conserva el original. No son mediciones nuevas ni demuestra precisión de 5 km. Puede cancelarse con Ctrl+C; una ejecución interrumpida deja un registro parcial recuperable. La escritura respeta los mismos límites de tamaño que el lector; no genera silenciosamente un registro demasiado grande para reproducirse.

## Selección progresiva e incertidumbre

La búsqueda predeterminada **regional-v3.1** incorpora el catálogo ampliado. Reserva referencias principales y exploración global, explora áreas cercanas aún no medidas y usa señales rápidas, diversidad ASN y posibles cortes para ordenar las demás sondas. Los destinos declarados pueden orientar la siguiente selección, pero no eliminan ninguna rama de la región principal.

El informe separa **región principal** e **hipótesis con sondas declaradas**. La segunda añade todas las declaraciones medidas con su incertidumbre nominal; si se contradicen, se retira esa hipótesis y su color. No se eliminan declaraciones incómodas para mantener una región pequeña. Una región condicional tampoco es un intervalo de confianza ni una ubicación física verificada.

El modo --planner information conserva el planificador anterior, restringido a referencias principales, para comparar. --planner legacy conserva la selección antigua.

El planificador **information-v2.1** comienza con cobertura global y separación de emplazamientos. Después combina:

- Aproximadamente 10 % de exploración global y 25 % de referencias capaces de distinguir distintas partes de la región.
- Aproximadamente 25 % de búsqueda cercana a señales rápidas. Baja al 10 % cuando ya responden ocho referencias a menos de 250 km de la más rápida, para evitar concentrar casi todo el presupuesto en una ciudad.
- El resto prioriza posibles recortes geométricos, diversidad de sitios y ASN y penaliza fallos. Descuenta recortes hipotéticos repetidos dentro de una tanda.
- Un hueco vuelve a medir una cota útil o variable; conserva mínimos y muestras anteriores.

Las cuotas se redondean y se adaptan a los datos disponibles. La selección estima RTT hipotéticos mediante referencias próximas y tres escenarios amplios. Evalúa esos escenarios sobre 192 puntos estratificados por área de la región completa, con margen para el tamaño de las celdas. **Esta aproximación solo ordena las sondas: no altera ni elimina celdas, muestras o cotas reales.** La puntuación mostrada no es probabilidad ni reducción observada.

Se prefieren sitios nuevos y se evitan IP o SiteId repetidos dentro de una tanda. Se conservan hasta 100 muestras por referencia. Un ASN diferente favorece diversidad, sin demostrar independencia de rutas; si falta, se usa un prefijo IPv4 /24 o IPv6 /48 solo para programar sondeos.

Se mantienen todas las ramas geográficas compatibles, incluidos antimeridiano y polos. El color es **afinidad relativa de red, no probabilidad de ubicación**. El área y radio cubren la malla retenida, condicionados al catálogo y modelo. El punto es su centro geométrico: no es domicilio, error real ni intervalo del 95 %. Las cotas incompatibles retiran el punto y el color.

No se resta latencia de acceso ni se reduce la velocidad de propagación para encoger la zona. Se mantienen 299,792458 km/ms y la incertidumbre de cada landmark (25 km en anchors; 50 km nominales en declaraciones). Las muestras antiguas usan su margen original de 1 ms. Las nuevas muestras LIVE elapsed-upper-v1 cronometran con Stopwatch toda la operación de envío/respuesta, incluyen sobrecoste local y conservan un margen nominal de reloj de al menos 0,05 ms, sustituyendo únicamente la cuantización entera. No se interpreta un cero redondeado como RTT nulo. Si se configura holgura adicional por encima de 1 ms, se conserva ese exceso. El método de cada muestra queda congelado en el JSONL. Este margen de reloj es una hipótesis de ingeniería, no una calibración estadística del equipo.

VPN, túneles, rutas indirectas y errores del catálogo limitan lo identificable. Un RTT alto no prueba lejanía. El presupuesto de celdas puede limitar la discretización; área y radio no se fuerzan a disminuir. Sin respuestas realmente rápidas y referencias fiables, la escala de ciudad puede no ser identificable.

La auditoría muestra concentración a 250 km, ASN, cotas contenidas en otra, primera ronda con el mejor RTT y sensibilidad al cambiar el objetivo de resolución a una cuarta parte con hasta 200 000 celdas. Una cota redundante al final pudo ser útil antes. Véase [el estudio con mediciones reales](PLANNER-STUDY.md).

## Datos y registros locales

El catálogo integra **1992 endpoints de anchors** y **7013 endpoints adicionales de sondas públicas**, 9005 en total, sin IP duplicadas. Los adicionales representan 5453 sitios de 133 países; 64 endpoints están declarados en España (26 IPv4 y 38 IPv6). Son 2451 IPv4 y 4562 IPv6 adicionales en todo el mundo. Varias IP pueden representar un sitio; se sondea solo la familia seleccionada. Coordenadas, marcado de verificación e incertidumbre nominal de los anchors son metadatos suministrados, no una validación nueva.

Las sondas adicionales conservan locationVerified=false y coordinateEvidence=operator-declared. Se filtraron estado público/conectado, conectividad y estabilidad de 30 días, disputas de geolocalización, NAT IPv4 conocido, etiquetas VPN/túnel/anycast e IP duplicadas. Esto no certifica ubicación, ausencia de túneles ni respuesta ICMP entrante. El margen de 50 km no cubre necesariamente cualquier error de declaración. Las coordenadas públicas además tienen ofuscación de privacidad. Data/regional-manifest.json documenta fuentes, filtros y hashes; el recurso comprimido verifica su SHA-256 al cargar.

tools/Build-RegionalCatalog.ps1 es una herramienta auxiliar para regenerar el snapshot durante el desarrollo; descarga solo metadatos públicos y guarda caché en obj/. No forma parte de LIVE, REPLAY ni del ejecutable. No se consumen mediciones remotas de Atlas ni se pide al servicio que estime la ubicación del usuario.

El mapa Natural Earth y el índice DB-IP ASN Lite (unos 3,3 MB) están incorporados como recursos. No se incluyen las bases grandes de ciudades o routers. Consulta las atribuciones y condiciones con notices.

~~~powershell
.\dist\win-x64\NetLocate.exe export-catalog .\landmarks.json
.\dist\win-x64\NetLocate.exe live --catalog .\landmarks.json --out .\otra-medicion
.\dist\win-x64\NetLocate.exe notices
~~~

El catálogo opcional admite hasta 20 000 registros con IP literal, coordenadas, procedencia, incertidumbre, SiteId y ASN opcional. Se excluyen referencias anycast. Las referencias sin verificar solo se sondean si están explícitamente marcadas operator-declared; únicamente participan en la hipótesis separada. La importación no autentica físicamente los destinos.

Los nuevos JSONL usan esquema 2 y motor netlocate-progressive-2: guardan versión del planificador, SHA-256 del catálogo, procedencia, ASN, opciones, método de reloj, muestras acumuladas y selección por ronda. REPLAY también acepta esquema 1 y motor 1 y descarta las estimaciones guardadas para reconstruirlas. Los registros antiguos mantienen sus cotas originales. Rechaza catálogo alterado, mezcla de conexiones, pérdida de muestras y rondas fuera de secuencia; puede recuperar una última línea truncada. La huella detecta cambios respecto a la cabecera, sin autenticar a un proveedor.

## Compilar y verificar

Con SDK estable .NET **10.0.402** y herramientas C++ de Visual Studio para Native AOT:

~~~powershell
.\publish.ps1
.\dist\win-x64\NetLocate.exe self-test
.\dist\win-x64\NetLocate.exe benchmark
~~~

El script acepta -Dotnet con la ruta del SDK; en su ausencia usa %LOCALAPPDATA%/NetLocate/BuildTools/dotnet10 si existe y después dotnet del PATH. global.json fija el SDK estable. PublishAot=true, analizadores y advertencias como errores; sin supresiones AOT. La serialización usa JsonSerializerContext y la reflexión JSON está deshabilitada.

La publicación usa bin/updates/win-x64, comprueba PE x64 y ausencia de cabecera CLR, ejecuta las pruebas desde el EXE y copia únicamente NetLocate.exe a dist/win-x64. Guarda tamaño, hash y evidencia en bin/updates/build-evidence.json. Sin paquetes NuGet de terceros de ejecución: solo .NET, fuentes del repositorio y dependencias normales del sistema operativo.

Las 52 pruebas C# cubren selección, conservación de la posición sintética conocida, separación de declaraciones, conflictos, cronometraje fraccional frente a registros antiguos, diversidad, determinismo, geometría, cancelación, serialización AOT, REPLAY, refinamiento de malla con procedencia y SVG. El benchmark mide cálculo sintético sin red.

qa/browser-check.cjs es una herramienta auxiliar de QA, excluida del producto. Playwright comprueba mapas, rondas, escritorio/móvil y ausencia de solicitudes externas. Configuración: NETLOCATE_PLAYWRIGHT y opcionalmente NETLOCATE_BROWSER; argumentos opcionales: ruta del informe demo y del informe LIVE.

## Límite de la validación

Las pruebas sintéticas y LIVE verifican ejecución y área compatible, no error geográfico real. No se ha utilizado una posición física conocida ni calibrado cobertura estadística. El programa puede devolver regiones muy amplias, datos insuficientes o contradicciones.

Las nuevas mediciones se guardan en measurements/city-study/, fuera de carpetas de compilación o limpieza; las capturas regenerables de QA están en bin/visual-qa/. Véase CITY-STUDY.md para los resultados de la ampliación y sus límites.
