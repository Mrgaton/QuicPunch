# QuicNet.Geolocation

Biblioteca C# (.NET 11) con catálogos de IP, ASN, referencias Atlas y routers IPmap compilados en la DLL. Consultar datos y recalcular mediciones existentes funciona offline. Los sondeos ICMP y traceroute miden la conexión del equipo que los ejecuta y necesitan red.

```csharp
using QuicNet.Geolocation;

var locator = new ProgressiveLocator();
using var analysis = locator.Start(new LocateRequest
{
    EnableNetwork = true,
    Budget = TimeSpan.FromSeconds(90)
}, update =>
{
    Console.WriteLine($"{update.Level}: {update.Region?.AreaKm2} km²");
    // update.Support contiene el campo de afinidad y sus hipótesis.
});
var result = await analysis.Completion;
```

El primer callback es síncrono antes de que `Start` retorne. Los posteriores son secuenciales y se ejecutan en el worker; publica sus resultados en el hilo de tu interfaz. `Cancel`/`Dispose` detienen el trabajo; `Completion` permite esperar su terminación.

`Region` conserva la zona compatible con las cotas de distancia máximas. Su representante es un centro geométrico, no la posición del dispositivo. `CoveringRadiusKm` cubre las celdas retenidas; no es un intervalo estadístico. Las cotas redundantes no atraen ese centro. `unbounded` no inventa un punto.

`DatabasePrior` contiene la ubicación publicada de la IP. Las coordenadas inferidas de routers siguen separadas en `Diagnostics.ConditionalRegion` y sus contradicciones se identifican en `Diagnostics`. Ninguna de estas declaraciones certifica el domicilio o municipio de una persona.

`Support` es un campo de **afinidad relativa, no probabilidad**. `SpatialSupportEstimator` agrupa señales a menos de 50 km, conserva hasta dieciséis sitios y usa el máximo de núcleos espaciales ponderados por el inverso del RTT mínimo al cuadrado. Las rutas o IP duplicadas no suman votos. La escala de cada núcleo es la mitad de su cota de distancia, con un mínimo de 50 km: es una heurística visual explícita, no una distancia medida ni una calibración estadística. Los picos requieren fuerza propia; una referencia lenta no hereda la de un vecino. Hipótesis de fuerza parecida se conservan por separado. Si existe una única declaración de router apartada por el diagnóstico, se excluye también del gradiente. Si la contradicción admite varias reparaciones y no hay una elección justificada, se retira el campo de afinidad.

El raster tiene como máximo 192 × 192 bytes. Cero significa fuera de la región compatible y 1–255 indica afinidad relativa dentro de ella. Está proyectado en Web Mercator y conserva regiones desconectadas. `Hints` y `Peaks` describen su procedencia. No colorea resultados sin señales, sin límites útiles o contradictorios. `Replay` reproduce el campo usando muestras y declaraciones congeladas.

Las dos primeras tandas reproducen el análisis rápido completo. Los presupuestos largos continúan sin sustituir sus muestras y vuelven a medir las mejores referencias; se conserva siempre el mínimo nativo. Las referencias nuevas se seleccionan entre emplazamientos aún no medidos, incluidos los caminos de selección regional y de mismo ASN.

`WorkbenchSession(record: false)` trabaja en memoria sin crear archivos. `Preview()` devuelve el estado y los contadores sin copiar el historial de paquetes. `Version` permite actualizar una interfaz únicamente si cambia el estado. El registro opcional por defecto sigue disponible para integraciones que necesiten conservar y reanalizar mediciones.

El RTT por sí solo no mide distancia exacta; obtener probabilidades geográficas calibradas necesitaría datos de validación y un modelo de error. Referencias de investigación:

- [Constraint-Based Geolocation, IMC 2004](https://conferences.sigcomm.org/imc/2004/papers/p288-gueye.pdf).
- [Dragoon: Advanced Modelling of IP Geolocation](https://arxiv.org/abs/2006.16895).

La aplicación de prueba usa esta biblioteca directamente. Los datos están en `Data/`; sus ediciones y hashes constan en `manifest.json`. Conserva `Data/NOTICE.txt` al distribuirla.

`LocationUpdate.Localities` añade una interpretación con nombres sin sustituir `Region`. El catálogo GeoNames contiene localidades de todo el mundo (`cities500`) y todos los núcleos poblados del extracto español, incluidos pueblos de menos de 500 habitantes. Para España, el municipio procede de la relación administrativa ADM3 del propio catálogo. Para otros países no se inventa una equivalencia entre localidad y municipio. Son puntos y nombres, no polígonos de límites municipales. La búsqueda no favorece capitales por población.

El catálogo `localities.bin` usa páginas comprimidas por grado geográfico (QPL2), un índice pequeño y una caché acotada de 128 páginas. Consultar una zona no carga todo el nomenclátor en memoria. `LocalityCatalog.Near`, `MatchPublished` e `InBounds` funcionan sin red. El generador reproducible es `python scripts/build-locality-catalog.py`; las descargas temporales se guardan en `.artifacts/locality-sources/`, y las fuentes, licencia, cobertura y hashes finales en `Data/localities-manifest.json`. El mapa pide al servidor local hasta 400 etiquetas del área visible. Las teselas de calles siguen siendo opcionales.

`LatencyPositionEstimator` realiza un ajuste **condicional y heurístico** de varias latencias: estima un retardo de acceso común, reduce el peso del jitter y explora varios modelos de propagación/ruta. Usa solo referencias principales aceptadas y emplazamientos independientes. No usa ciudades de routers para certificar el municipio. La exploración detallada puede usar este ajuste para buscar nuevas referencias próximas, conservando los sondeos rápidos originales. Las mediciones reales usan el tiempo completo de envío y recepción con `Stopwatch`, evitando presentar el RTT entero como precisión submilisegundo.

La sensibilidad entre modelos no es un intervalo de confianza, y tiene un mínimo explícito de 25 km (150 km con geometría insuficiente). La malla, la dispersión del modelo y la región físicamente compatible son magnitudes diferentes. El resultado muestra alternativas y desacuerdos con la base de IP; no convierte el ajuste en domicilio, porcentaje de certeza ni municipio confirmado. Si los pings fallan, muestra la referencia publicada de la IP y su procedencia; si tampoco existe esa referencia, explica la falta de evidencia. La base MMDB opcional ya no oculta un dato disponible en la base integrada cuando le falta ese registro. Las conexiones Tor no se localizan.

Fuentes consultadas para el rediseño:

- [GeoNames: formato, cobertura de cities500 y licencia CC BY 4.0](https://download.geonames.org/export/dump/readme.txt).
- [RIPE IPmap: el score ordena hipótesis y no mide un porcentaje de certeza](https://ipmap.ripe.net/docs/01.manual/).
- [RIPE Atlas: coordenadas de sondas declaradas y ofuscadas](https://atlas.ripe.net/docs/faq/security-and-privacy/).
- [MaxMind: límites de precisión, VPN, redes móviles y resultados incompletos](https://support.maxmind.com/knowledge-base/articles/maxmind-geolocation-accuracy).

La validación automatizada usa escenarios sintéticos y catálogos congelados. Recuperar una posición en una simulación no demuestra esa precisión en Internet: antes de calibrar porcentajes o afirmar precisión municipal hacen falta mediciones de ubicaciones consentidas conocidas, separadas por operador, país, tecnología de acceso, IPv4/IPv6 y uso de VPN, evaluadas fuera del conjunto de ajuste.

```powershell
dotnet build QuicNet.Geolocation -c Release
dotnet pack QuicNet.Geolocation -c Release
dotnet run --project QuicNet.Geolocation.App -c Release -- --self-test
```
