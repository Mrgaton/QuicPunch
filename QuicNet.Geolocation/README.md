# QuicNet.Geolocation 1.2 — búsqueda jerárquica y diagnóstico local

Biblioteca compilada y comprobada con la aplicación de prueba y mediciones directas. La revisión del 19 de octubre de 2026 incorpora catálogo mundial, destinos españoles adicionales y calibración CBG. La precisión municipal no está certificada. Informe: `docs/ANALISIS_ANCLAS_2026-10-19.md`.

La revisión de topología añade selección de rutas diversas, recorridos ICMP completos repetidos y un ranking regional explicable (`Enhanced.RouteLocation`). Informe y comparación con los tres presets anteriores: `docs/ANALISIS_RUTAS_2026-10-19.md`. La topología modifica la afinidad y la selección de mediciones; no convierte routers del operador en posiciones del dispositivo ni recorta las regiones RTT/CBG.

## Inicio

Requiere .NET SDK 10 para compilar y .NET 10 para ejecutar una distribución dependiente del framework. Los recursos iniciales ya están en `Data/`; no se descargan al iniciar la biblioteca. Python 3.11+ solo se necesita para regenerar catálogos o utilizar Smart Edit.

Desde la carpeta del proyecto, estos son los comandos de uso:

```powershell
dotnet run --project Cli -- --help
# Offline: no se envían sondeos y no se busca remotamente tu IP.
dotnet run --project Cli -- --out offline.json
# Mediciones expresamente activadas. Ctrl+C conserva resultados parciales.
dotnet run --project Cli -- --network --seconds 90 --rounds 6 --out resultado.json --geojson mapa.geojson
# PMTU IPv4 + TCP de destinos que han declarado permiso para mediciones.
dotnet run --project Cli -- --network --mtu --tcp --out resultado.json
# Reproducción de un archivo local, sin sondeos.
dotnet run --project Cli -- --replay resultado.json --geojson mapa.geojson
```

La salida durante las mediciones es JSON por evento. El mapa GeoJSON puede abrirse con una herramienta GIS local. `tools/map-viewer.html` permite cargar el archivo sin CDN, mapas remotos ni servidor web. Los pesos son afinidad cualitativa, no porcentajes de acierto.

## API principal

```csharp
using QuicNet.Geolocation;

using var run = new ProgressiveLocator().Start(new LocateRequest
{
    EnableNetwork = true,
    UseHierarchicalEngine = true, // valor predeterminado
    EnableTunnelAssessment = true,
    EnableTopology = true,
    EnablePathMtu = true,
    EnableTcp = false,
    Rounds = 6,
    BatchSize = 12,
    PacketsPerSecond = 10,
    MaximumProbes = 1500,
    Budget = TimeSpan.FromSeconds(90)
}, update =>
{
    Console.WriteLine($"{update.Phase}: {update.MeasuredLandmarks} referencias");
    // update.Enhanced.Hierarchy: alternativas geográficas, no municipios confirmados.
    // update.Enhanced.Tunnel: rutas efectivas, interfaces y evidencia PMTU.
});
AnalysisResult result = await run.Completion;
```

Los callbacks son síncronos y seriales en un trabajador. La integración gráfica debe trasladarlos a su hilo de interfaz. Si un callback falla, la finalización comunica el fallo. No cambies sus colecciones. Una cancelación ordinaria devuelve `cancelled-partial` y las observaciones completadas.

## Cambios implementados

- Motor jerárquico predeterminado con alternativas de país/región/área local, exploración global reservada, diversidad de emplazamientos y red, elección de referencias informativas y repetición intercalada. Reutiliza `InformationLandmarkPlanner` en lugar de dejarlo aislado de la API principal.
- Huellas relativas de RTT: mínimo/mediana/MAD, misma intersección de referencias al restar retardos comunes, desacuerdo de rankings, selección por ASN y señales topológicas acotadas. Se requieren perfiles empíricos consentidos; sin ellos el estado es `no-trained-data`.
- Traceroute ICMP repetido, routers compartidos, prefijos comunes y divergencias. Los saltos perdidos y las rutas balanceadas no se inventan. Las diferencias entre RTT de saltos consecutivos no se interpretan como distancias de enlaces.
- Grafo de nodos y aristas estables tras repeticiones, coincidencias después de huecos y tramos privados del operador tratados exclusivamente como identificadores de esta sesión. Los contrastes entre RTT **de destinos**, su variabilidad y las ramas observadas ponderan hipótesis regionales. El número de saltos no se convierte en kilómetros. Es afinidad heurística de red, no un solucionador TBG completo ni una probabilidad calibrada.
- Presets de calidad: Rápido hasta 120 s/8 rutas/24 TTL/2 repeticiones; Equilibrado 300 s/16 rutas/28 TTL/3 repeticiones; Detallado 600 s/24 rutas/32 TTL/3 repeticiones. Límites compartidos de 2.000/4.000/8.000 lanzamientos y máximo 20 por segundo; las rutas se miden con concurrencia acotada a tres. Finalizan antes si completan sus tandas.
- Diagnóstico local de túnel por interfaz/ruta efectiva, MTU y Path MTU IPv4 con DF, controles pequeños, límites inferior/superior y estados indeterminados. No identifica VPN exclusivamente por fallar 1472 bytes y no intenta saltarse la VPN.
- TCP opcional, separado de ICMP, hacia servicios autorizados en el catálogo. No se hace una petición HTTP ni una consulta de geolocalización externa.
- Presupuesto compartido de lanzamientos, tiempo y concurrencia; conservación de tandas y muestras parciales; detección de cambios de interfaz/origen entre rondas; suspensión de perfiles con rutas de túnel/directas mezcladas.
- Capas separadas: región física primaria, refinamiento condicionado por declaraciones del operador, afinidad, hipótesis y localidades. Encontrar una IP de router ya no basta para etiquetar el resultado `RouteCorroborated`.
- .NET 10, consola, metadatos de prefijos offline, captura local de perfiles, replay, exportación GeoJSON, escrituras JSON atómicas y límites de tamaño de entrada.

## Catálogos: qué hay realmente

`Data/coverage.json` y `Data/manifest.json` describen el contenido exacto: **2.158 direcciones, 883 grupos de emplazamiento, 101 direcciones españolas en 63 grupos y 267.958 localidades GeoNames**. `spanish-coverage.json` enumera las 17 comunidades y las dos ciudades autónomas, incluidos los huecos. RIPE aporta 1.080 registros activos mundiales; se agrupan emplazamientos próximos y se añaden servidores públicos españoles de medición.

No se inventan servidores por municipio. Las coordenadas son declaraciones del operador (`LocationVerified=false`); una localidad GeoNames no implica un servidor allí. El catálogo alcanza 13 comunidades y Melilla. Faltan Aragón, Cantabria, Navarra, La Rioja y Ceuta. La comprobación directa final desde esta conexión obtuvo respuesta ICMP en 84 de 90 destinos españoles IPv4; Melilla no respondió. Esto describe esta sesión, no disponibilidad universal.

Desde la raíz del repositorio:

```powershell
# Descarga explícita y solo al preparar el paquete; conserva snapshots existentes.
python QuicNet.Geolocation/tools/fetch_measurement_sources.py --download
python QuicNet.Geolocation/tools/build_latency_calibration.py
python QuicNet.Geolocation/tools/build_catalogs.py
python QuicNet.Geolocation/tools/test_catalog_builder.py
```

El constructor filtra referencias desactivadas, privadas y duplicadas; conserva procedencia y hashes. El constructor no resuelve DNS ni descarga datos durante la medición. Las cachés de preparación están en `.artifacts/geolocation-catalogs/raw`; los recursos generados permanecen en `Data/`. Para actualizar una fuente, sustituye su snapshot explícitamente y vuelve a generar todos los recursos relacionados. `build_catalogs.py --raw RUTA` permite construir desde otro snapshot local; sin snapshot utiliza la semilla pequeña incluida. No son polígonos municipales oficiales.

El workspace incluye `city.bin`, `asn.bin` y `routers.bin`; el manifiesto registra sus tamaños, recuentos y hashes reales. Las declaraciones de routers permanecen como infraestructura blanda, sin convertirse en cotas primarias verificadas. `--metadata prefijos.json` permite añadir información local.

### Preparación y actualización de las bases IP compactas

Las tres tablas IP se distribuyen como GLP2, integradas en la DLL. El compresor y el importador son herramientas de preparación en Python y no se copian al ejecutable, a la publicación ni al paquete NuGet. Cada compilación comprueba las cabeceras: si detecta una tabla GLP1 nueva/actualizada, ejecuta automáticamente la migración antes de incrustarla. Cuando los recursos ya están preparados, la compilación no necesita Python. El lector C# solo descomprime las páginas consultadas.

```powershell
# Dependencias exclusivamente de preparación; se puede usar un entorno virtual.
python -m pip install -r QuicNet.Geolocation/tools/requirements-catalogs.txt

# Migrar las tablas GLP1 nuevas/actualizadas que haya en Data/.
python scripts/prepare-geolocation.py

# Importar una edición nueva del MMDB y producir automáticamente GLP2 verificado.
python scripts/prepare-geolocation.py --iptrust "C:/ruta/iptrust-geolocation.mmdb" --verify

# Comprobación rápida de formato, directorio, recuentos, tamaños y hashes.
python scripts/prepare-geolocation.py --check

# Comprobación completa: reconstruye y verifica todas las filas de GLP2.
python scripts/prepare-geolocation.py --check --verify

# Forzar además la comprobación de hashes durante una compilación.
# CatalogPython permite elegir el intérprete de preparación.
dotnet build QuicNet.Geolocation -c Release -p:PrepareGeolocationCatalogs=true
```

`--data DIRECTORIO` permite preparar otra carpeta que contenga las tablas y su `manifest.json`. El importador directo `scripts/import-iptrust-geolocation.py` también termina en este mismo flujo. `build_catalogs.py` conserva y comprueba las tablas compactas al regenerar los otros catálogos. `scripts/build-desktop-release.ps1` ejecuta la preparación antes de publicar; `-CatalogPython` permite seleccionar el intérprete de preparación.

Cada conversión compara el número de filas y un SHA-256 canónico calculado a partir de ambos formatos, incluyendo todos los rangos IPv4/IPv6, los bits originales float32 y los textos UTF-8. Solo entonces reemplaza el archivo y actualiza su manifiesto. Los cambios usan reemplazos atómicos, un bloqueo entre procesos y un pequeño registro recuperable si el proceso termina entre el reemplazo de la tabla y el del manifiesto. Una tabla GLP2 sin cambios solo se comprueba; no se recomprime. El modo `--verify` vuelve a contrastar el hash de las filas reconstruidas. Las fuentes nuevas deben conservar su procedencia y fecha en el manifiesto; importar IPTrust las obtiene del MMDB.

GLP2 separa páginas IPv4 e IPv6, almacena las direcciones de los índices en 4/16 bytes y codifica cada rango mediante diferencia respecto al anterior y longitud como enteros variables. Un diccionario global conserva coordenadas y nombres únicos; cada página traduce identificadores locales de uno o dos bytes a identificadores globales. Las páginas usan Brotli y el diccionario compartido también se comprime. Un identificador global no se limita a `ushort`: este snapshot tiene 106.427 ubicaciones distintas.

En RAM, `IpCatalogTable` conserva el diccionario como bytes UTF-8 y offsets, sin construir una cadena ni un objeto por cada rango. Las páginas usan arrays `uint` para IPv4 y `UInt128` para IPv6; los identificadores locales mantienen su ancho de uno/dos bytes. La caché está limitada por tabla a 32 páginas y 2 MiB de arrays numéricos; el diccionario, los pequeños índices y hasta 512 resultados materializados se contabilizan aparte. Las consultas calientes no crean arrays de direcciones ni materializan de nuevo las cadenas. `localities.bin` conserva su formato QPL2 y su caché propios.

Medición local del 23 de octubre de 2026, Windows x64, .NET 10.0.13, Release: mediana de tres procesos independientes por lector. Ambos leen mediante FileStream las mismas 1.403 direcciones repartidas por las páginas del catálogo de IP; el lector antiguo está congelado únicamente en el proyecto de pruebas. La memoria es el incremento de heap gestionado después de GC al consultar 32 páginas IPv4, incluyendo el diccionario, no la RAM total del proceso. El coste de la primera consulta incluye inicialización y JIT; no se vacía la caché de disco del sistema.

| Medida | GLP1 anterior | GLP2 |
|---|---:|---:|
| Tabla de ubicación IP | 60,84 MiB | 11,24 MiB |
| Las tres tablas IP | 68,74 MiB | 14,35 MiB |
| Tablas IP + localidades sin modificar | 83,27 MiB | 28,88 MiB |
| Heap retenido con 32 páginas IPv4 | 53,47 MiB | 4,67 MiB |
| Primera consulta | 12,77 ms | 39,99 ms |
| Consulta caliente, media de 100.000 | 0,621 µs | 0,379 µs |
| Consulta repartida, p50 | 2,447 ms | 0,171 ms |
| Consulta repartida, p95 | 23,709 ms | 0,414 ms |

La carga inicial de GLP2 es mayor porque prepara el diccionario compartido; después reduce la memoria retenida y el trabajo de cada página. Las medidas dependen del equipo y del patrón de consulta.

```powershell
python -m unittest discover -s QuicNet.Geolocation/tools -p "test_*.py"
python QuicNet.Geolocation/tests/CatalogTests/generate-fixtures.py
dotnet run --project QuicNet.Geolocation/tests/CatalogTests -c Release -- fixtures QuicNet.Geolocation/tests/CatalogTests/obj/fixtures
python QuicNet.Geolocation/tests/CatalogTests/benchmark.py
```

El benchmark regenera el formato antiguo en `tests/CatalogTests/obj/` cuando no se le pasa `--baseline DIRECTORIO`. Verifica todos los campos de todas las filas mediante el lector C#, consulta inicio/final/punto intermedio y comprueba los huecos; luego mide ambos lectores en procesos distintos. `--output ARCHIVO.json` guarda todas las repeticiones y medianas. La verificación contra los GLP1 originales de esta migración superó 6.994.964 rangos y 22.569.736 consultas. Las fixtures adicionales cubren UTF-8, ceros con signo, identificadores locales de ambos anchos, máximos IPv4/IPv6/ASN, rangos con huecos, concurrencia y datos corruptos. La aplicación de prueba superó sus 179 comprobaciones offline y la integración `--test-geolocation --loopback-only`.

La calibración incluye 60 relaciones por destino/familia de la malla pública RIPE. Implementa la envolvente inferior de CBG, sin copiar código ajeno: cada ancla obtiene su propia pendiente e intercepto. Se congela en el catálogo exportado y caduca para análisis nuevos. Un RTT inferior al intercepto invalida esa línea; una intersección contradictoria queda explícita. `RegionalRefinement.Region` conserva la hipótesis física con coordenadas declaradas; `CalibratedRegion` contiene la hipótesis empírica separada. Su centro es geométrico, no una ciudad detectada.

Para repetir los tres presets directamente, sin web ni coordenadas conocidas:

```powershell
dotnet run --project QuicNet.Geolocation/tests/PresetAnalysis -c Analysis
dotnet run --project QuicNet.Geolocation/tests/PresetAnalysis -c Analysis -- --audit-catalog
```

Los resultados se guardan fuera del repositorio, en `%LOCALAPPDATA%/QuicNet/GeoLatency/diagnostics`, con muestras, ajustes y hashes. No se envían coordenadas del dispositivo ni se descargan catálogos durante la medición.

## Perfiles locales de entrenamiento

```powershell
dotnet run --project Cli -- --replay resultado.json --capture perfiles.json --consent --latitude 41.5 --longitude 2.0 --country ES --region Cataluña --locality "Localidad conocida" --group "conexion-anonima-001"
dotnet run --project Cli -- --network --profiles perfiles.json --asn 12345 --out resultado-nuevo.json
```

Las coordenadas del ejemplo son ilustrativas: reemplázalas por una posición verdadera obtenida con consentimiento. No etiquetes usando la propia predicción del programa. Se necesitan al menos cuatro emplazamientos con mediciones repetidas compatibles. Los perfiles se guardan exclusivamente en disco y contienen información sensible de ubicación y rutas. No hay telemetría ni envío automático. Utiliza grupos anónimos para que una conexión repetida no multiplique su peso.

## Límites deliberados de esta entrega

La precisión municipal no está demostrada. CBG aporta calibración empírica entre anclas, pero no probabilidades calibradas ni entrenamiento de conexiones residenciales. PMTU IPv6 informa de capacidad no soportada por el transporte portable, en vez de dar una falsa medida. El traceroute incorporado **no es Paris traceroute**: se proporciona una interfaz y un lector de resultados scamper/Paris locales con comprobación de destino y antigüedad, pero no se distribuye ni se ejecuta automáticamente ese binario. La elección de ruta en Linux/macOS se basa en el origen elegido por el kernel para UDP y puede diferir de políticas ICMP; Windows consulta `GetBestRoute2`.

Los túneles en el router, proxies de aplicación y políticas de filtrado pueden no detectarse. MTU reducida también puede aparecer sin VPN. Un RTT bajo no demuestra municipio ni domicilio. La biblioteca solo estima la conexión desde la que se ejecutan las mediciones.

Más detalles: `docs/ARQUITECTURA_Y_CAMBIOS.md`, `docs/ESTADO_DE_ENTREGA.md`, `Data/NOTICE.txt`. La licencia original no estaba adjunta; `LICENSE-NOTICE.md` evita atribuir una licencia no facilitada.
