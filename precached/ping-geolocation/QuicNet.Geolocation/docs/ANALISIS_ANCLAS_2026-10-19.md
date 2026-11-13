# Anclas, estimador y mediciones directas — 19/10/2026

Continuación con rutas compartidas y comparación sobre las mismas mediciones: [Análisis de topología](ANALISIS_RUTAS_2026-10-19.md). Las tandas de este documento se conservan como histórico.

La revisión amplía el catálogo y cambia la hipótesis puntual por una región CBG explícitamente condicional. **Todavía no ofrece una ancla geográficamente certificada por cada comunidad ni precisión municipal demostrada.** Barcelona se usa únicamente para evaluar el resultado después de medir; sus coordenadas no se suministran al estimador.

## Causas reproducidas

La semilla anterior tenía 45 direcciones, 22 grupos de emplazamiento y 24 etiquetas de infraestructura. En los presets originales solo se exploraban 12/24 destinos. Las referencias españolas se concentraban en Madrid, Cataluña y Murcia. No había un nomenclátor municipal completo en el recurso realmente cargado.

El ajuste anterior escogía por cuartil un supuesto retardo común de acceso. Ese cuartil no minimizaba su propia función de coste y podía superar el RTT completo más rápido. Además, interpretaba desvíos de rutas diferentes mediante un mismo modelo de propagación. Su óptimo terminaba en el mar, a 607/724/662 km del centro de Barcelona usado como referencia de evaluación. Barcelona seguía satisfaciendo las cotas máximas: el punto no demostraba su exclusión.

Se corrigió la optimización del retardo con la pérdida convexa asimétrica real, acotada por el menor RTT completo. La capa condicional utiliza ahora CBG cuando dispone de calibraciones suficientes; mantiene separado el ajuste antiguo para catálogos sin calibración y para inspección técnica.

## Fuentes y construcción

| Fuente | Incorporación y límites |
|---|---|
| [RIPE Atlas: anclas](https://atlas.ripe.net/docs/apis/rest-api-reference/anchors) | Snapshot completo, con 1.080 registros activos. Se excluyen retirados, desactivados, IP privadas y duplicadas. No se presupone que sus coordenadas estén certificadas. |
| [Servicios públicos de las anclas RIPE](https://atlas.ripe.net/docs/howtos/anchors/anchor-services) | Publican ping, traceroute y servicios de medición para sistemas externos; se usan IP literales. |
| [Directorio público de servidores Speedtest](https://www.speedtest.net/api/js/servers?engine=js&search=Spain&limit=100) | Selección española por ciudades y diversidad de proveedores. DNS solo al preparar el recurso; se descartan IP compartidas por regiones distintas. La cobertura publicada no demuestra disponibilidad ni posición física. |
| [RedIRIS LibreSpeed](https://speedtest.rediris.es/) | El operador publica los nodos de Madrid, CESGA–Santiago y RedCAYLE–León. Se incorporan sus IP literales con la ciudad publicada y un margen nominal de 50 km; no se atribuye GPS al servidor. |
| [GeoNames](https://download.geonames.org/export/dump/) | ES.zip, cities500.zip y divisiones administrativas; 267.958 localidades. Atribución CC BY 4.0. Una localidad no implica una ancla. |
| [Malla pública entre anclas RIPE](https://atlas.ripe.net/docs/apis/rest-api-reference/anchor-measurements/) | 60 relaciones por destino/familia, incluyendo 22 españolas. Se descargan resultados existentes; no se crean mediciones remotas ni se consumen créditos. |

El paquete final contiene **2.158 direcciones en 883 grupos de emplazamiento, 113 países, 101 direcciones españolas en 63 grupos y 267.958 localidades**. `Data/manifest.json` registra hashes, tamaños y recuentos reales de los recursos, incluidas las bases IP heredadas presentes. Las descargas y cachés se limitan a la preparación; el runtime no consulta APIs de geolocalización ni descubre la IP pública.

Los scripts de reproducción están en `tools/fetch_measurement_sources.py`, `build_latency_calibration.py`, `build_catalogs.py` y `test_catalog_builder.py`. Las entradas regenerables están en `.artifacts/geolocation-catalogs/raw`; los recursos distribuidos están en `Data/`.

## Método implementado

La implementación independiente sigue [Constraint-Based Geolocation, Gueye et al., IMC 2004](https://conferences.sigcomm.org/imc/2004/papers/p288-gueye.pdf). Para cada destino se obtiene una envolvente lineal inferior de RTT frente a distancia entre anclas. La envolvente convexa inferior resuelve el programa lineal de dos variables:

`max Σ distancia · pendiente + n · intercepto`, sujeto a `pendiente · distancia + intercepto ≤ RTT`, `intercepto ≥ 0` y pendiente no inferior a la propagación de ida y vuelta en vacío.

Las medidas de la malla llevan una tolerancia explícita de 1 ms. Se auditan filas incompatibles incluso con propagación en vacío. Al aplicar la línea: `radio = (RTT + margen - intercepto) / pendiente + incertidumbre declarada`, limitado por la cota física conservadora con coordenadas declaradas. Un RTT inferior al intercepto invalida esa calibración en vez de crear un círculo de radio cero.

El modelo sigue siendo empírico: la dirección de las medidas públicas difiere de la del cliente, las rutas pueden cambiar, y las coordenadas son declaraciones. Por eso `RegionalRefinement.Region` conserva la hipótesis física condicionada a esas coordenadas; `CalibratedRegion` conserva otra hipótesis separada. Una contradicción permanece visible. La región primaria nunca incorpora coordenadas descargadas como si estuvieran verificadas.

El centro CBG es una referencia geométrica de la región. `Method=cbg-region-centroid`; `ResidualMs` y `AccessDelayMs` son nulos porque no existe ese ajuste temporal. Su extensión no es error medido ni intervalo de confianza. Las calibraciones quedan congeladas por dirección en el catálogo de la sesión y se consideran caducadas para nuevas mediciones después de 30 días.

Los tres presets comparten 24 destinos y dos muestras iniciales. Después se recorren las regiones disponibles del país con mayor afinidad, se reservan sondeos globales y se repiten dos destinos rápidos. Rápido usa 3×24 con 3 muestras; Equilibrado, 5×32 con 4; Detallado, hasta 8×48 con 6. Todos respetan 20 lanzamientos/s y 1.500 lanzamientos máximos; las trazas comparten ese presupuesto. El contador incluye intentos, no todos los paquetes del sistema operativo.

## Cobertura territorial comprobada

`Data/spanish-coverage.json` enumera las 19 regiones y conserva ceros. Hay destinos publicados en **13 de 17 comunidades** y Melilla. Faltan Aragón, Cantabria, Navarra, La Rioja y Ceuta. Melilla figura en el catálogo, pero no respondió al ping de esta conexión.

La auditoría final envió tres pings a cada uno de los 90 destinos españoles IPv4: **84 respondieron; seis no**. CESGA–Santiago respondió a dos de tres y León a tres de tres; el nodo de RedIRIS–Madrid no respondió. Los intentos y RTT están en el archivo de auditoría. Esto no certifica disponibilidad desde otras redes.

Se examinaron además 75 referencias de routers españoles publicadas por RIPE IPmap para cubrir huecos: 22 respondieron, repartidas entre Aragón, Cantabria y Galicia; las candidatas de Navarra y La Rioja no respondieron. Entre las etiquetas aparecieron latencias aparentemente incompatibles con sus posiciones publicadas. **No se convierten esas etiquetas inferidas en anclas verificadas ni se añaden a las restricciones CBG.** El experimento conserva tanto candidatos como respuestas para revisión.

Para completar la cobertura fiable pendiente hacen falta destinos estables y autorizados, con posición documentada por su operador y calibración repetible. Tener una IP etiquetada por una base de geolocalización no satisface ese requisito.

## Validación

- Biblioteca, CLI y aplicación compiladas; cero errores. El proyecto grande de integración conserva seis advertencias CS8602 previas en pruebas de seguridad ajenas a geolocalización.
- `--self-test`: 157 comprobaciones, incluidos modelo físico, conflictos, catálogos, cancelación, límites, replay, afinidad y CBG sintético independiente en Barcelona, Madrid, Valencia, Sevilla y Sídney.
- `--self-test-web`: 23 comprobaciones; también pasa con reflexión JSON desactivada.
- Cuatro pruebas del constructor: programa lineal, filtros de anclas, formato UTF-8/antimeridiano, hashes y cobertura real.
- `--test-geolocation` y `--test-geolocation-workbench`: correctas; la primera incluye la geolocalización automática del proyecto principal.
- Tres pruebas de navegador: Blazor con calor condicional y 19 regiones; panel integrado real; banderas/localidades de contactos. La última necesitaba habilitar Tor en su fixture para comprobar una tarjeta Tor que el modo WAN oculta correctamente.

Las pruebas deterministas no prueban precisión en Internet. Los ensayos de red y su variación se documentan a continuación.

## Resultados reales conservados

Medición directa de `ProgressiveLocator.Start(WorkbenchSettings.Preset(...).ToRequest())`, en serie, IPv4, sin IP pública, perfiles ni posición conocida. Tiempo UTC de inicio de la tanda final: 2026-10-19 20:48:52. Los valores relativos de país no son probabilidades.

| Preset final | Tiempo | Muestras echo guardadas | Otros/no registrados | Lanzamientos | Destinos | Centro geométrico CBG | Extensión desde el centro | Estado |
|---|---:|---:|---:|---:|---:|---|---:|---|
| Rápido | 30.4 s | 167 | 77 | 244 | 70 | 40.4297, -3.6328 | 1503 km | time-budget-exhausted |
| Equilibrado | 82.6 s | 560 | 172 | 732 | 146 | 40.4297, -3.6328 | 1710 km | complete |
| Detallado | 157.5 s | 1178 | 322 | 1500 | 210 | 40.4297, -3.6328 | 1706 km | probe-budget-exhausted |

El resto de lanzamientos incluye traceroute y operaciones canceladas sin muestra final; no permite reconstruir un contador exacto de trazas a partir de este archivo.

Los tres centros finales son referencias geométricas próximas a Madrid, **no una detección del dispositivo en Madrid**. La región primaria continúa en `insufficient-data`: no hay coordenadas físicamente verificadas. Barcelona satisface todas las cotas CBG de los tres presets. La extensión de la región impide una conclusión municipal o autonómica fiable.

| Preset final | Referencia más rápida | RTT mínimo | RTT Barcelona–Meswifi | Cotas calibradas CBG |
|---|---|---:|---:|---:|
| Rápido | Alcobendas | 20.16 ms | 31.26 ms | 5 |
| Equilibrado | Madrid · VPSFree | 20.35 ms | 30.80 ms | 11 |
| Detallado | Madrid | 21.47 ms | 29.79 ms | 11 |

**Por qué ocurre:** los destinos de Madrid/Alcobendas han respondido más rápido que el de Barcelona en la tanda final. Con RTT altos, las distancias máximas resultantes son grandes. CBG conserva una región muy amplia; el algoritmo geométrico puede escoger su centro cerca del grupo de cotas más restrictivas. Eso no basta para distinguir la posición del cliente de rutas indirectas o retardos de acceso.

Una tanda anterior, conservada completa y con 2.155 destinos antes de añadir los tres nodos RedIRIS, encontró estas condiciones:

| Preset anterior | Lanzamientos | RTT Barcelona | Centro CBG | Distancia del centro al centro de Barcelona usado para evaluar | Extensión geométrica |
|---|---:|---:|---|---:|---:|
| Rápido | 300 | 2.01 ms | 41.3672, 1.5234 | 54 km | 430 km |
| Equilibrado | 724 | 1.73 ms | 41.3672, 1.7578 | 34 km | 387 km |
| Detallado | 1500 | 1.65 ms | 41.3672, 1.7578 | 34 km | 366 km |

Esos centros estaban en Cataluña y Barcelona seguía dentro de todas las cotas. La distancia al centro de una ciudad de referencia **no es precisión calibrada** ni error respecto al domicilio. La región seguía siendo amplia incluso en esa tanda.

La degradación se contrastó con `ping` de Windows al mismo destino público `185.210.244.3`: una muestra dio 30–98 ms y otra 34–664 ms. Se conserva la segunda salida sin editar. El cambio observado no se puede atribuir únicamente al estimador ni únicamente a sobrecarga del cronómetro .NET; el ping nativo también lo muestra. No se ha demostrado la causa de red concreta.

## Archivos de auditoría

- Última tanda: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\presets-20261019-204852Z`.
- Tanda anterior con latencias bajas: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\presets-20261019-203739Z`.
- Tanda intermedia con degradación y ping nativo: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\presets-20261019-204336Z`.
- Cobertura final: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\catalog-audit-20261019-204835Z`.
- Referencias de infraestructura candidatas y descartadas: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\infrastructure-audit-20261019-204134Z`.

Cada preset conserva `settings.json`, `archive.json`, `updates.json`, `execution.json`; `summary.json` se deriva de esos archivos. Las procedencias y hashes permiten distinguir las tandas. Los resultados únicos permanecen fuera de `.artifacts/`.

```powershell
dotnet run --project QuicNet.Geolocation/tests/PresetAnalysis -c Analysis
dotnet run --project QuicNet.Geolocation/tests/PresetAnalysis -c Analysis -- --audit-catalog
python QuicNet.Geolocation/tools/summarize_presets.py RUTA_RESULTADOS --reference-lat 41.3874 --reference-lon 2.1686
```

Las coordenadas de ese último comando solo evalúan archivos ya medidos; nunca se envían al estimador.
