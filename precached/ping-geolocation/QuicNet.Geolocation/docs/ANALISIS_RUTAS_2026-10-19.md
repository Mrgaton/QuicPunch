# Rutas compartidas y comparación de presets — 19/10/2026

Se ha implementado y ejecutado la revisión `hierarchical-3-topology`. En la conexión actual, **los tres presets ponen Cataluña como primera alternativa de afinidad de red**. Esto no confirma la comunidad del dispositivo. La topología aporta más información sobre los recorridos y sus dependencias; esta sesión no demuestra una mejora de precisión geográfica. La región CBG sigue siendo amplia.

Se midió directamente con `ProgressiveLocator`, sin servidor web, sin suministrar la posición de Barcelona, sin perfiles de entrenamiento y sin consultar una API de geolocalización. El centro de Barcelona solo se empleó después para comprobar si satisface las cotas guardadas.

## Qué ha cambiado

- `TopologyTracePlanner` elige destinos medidos de regiones, emplazamientos y ASN diversos. Las ramas observadas reducen la preferencia por recorridos redundantes. Se conservan destinos de control fuera del área principal.
- `RepeatedIcmpTraceProvider` realiza dos o tres barridos completos. Ya no abandona tras seis TTL silenciosos: puede encontrar respuestas públicas después de un tramo filtrado. Cada respuesta conserva su barrido. Hasta tres rutas se ejecutan concurrentemente con el mismo límite global de 20 lanzamientos por segundo.
- `AccessTopologyAnalyzer` reconoce nodos estables después de huecos y a distintos TTL. Exige repetición y coincidencia de las respuestas; excluye TTL variables, IP repetidas en varios TTL y el primer salto privado local. Solo crea aristas entre TTL consecutivos observados. No inventa enlaces a través de huecos.
- Los nodos privados/CGNAT posteriores al primer salto sirven como identificadores opacos **dentro de la sesión**. No se geolocalizan, no se convierten en anclas y no se comparan como identidades globales entre conexiones distintas. Las direcciones de infraestructura públicas continúan separadas de la posición del dispositivo.
- `TopologyLocationEstimator` combina la señal local de menor RTT, un ajuste robusto del patrón de latencias y contrastes entre los RTT **de destinos** con tramos compartidos. Reduce votos de sitios, áreas y ramas redundantes. Limita la contribución de pares que reutilizan el mismo destino. Cada zona conserva razones inspeccionables.
- `Enhanced.RouteLocation` expone zonas ordenadas, pérdidas del modelo, contrastes y limitaciones. El planificador utiliza varias alternativas para elegir mediciones posteriores. La web muestra sus razones y la calidad de los traceroutes.
- Replay reconstruye la topología y el ranking sin red. El validador admite 192 respuestas de ruta: tres barridos de hasta 64 TTL, manteniendo un límite de tamaño de entrada.

**La topología no recorta las regiones RTT/CBG ni convierte el número de saltos en distancia.** El mapa mantiene esas regiones y sus colores condicionales; el ranking por rutas se presenta por separado. La ausencia de respuesta no equivale a lejanía, cercanía ni latencia cero.

## Presupuestos de calidad

| Preset | Tiempo máximo | Destinos trazados, máximo | TTL por barrido | Barridos | Lanzamientos, máximo |
|---|---:|---:|---:|---:|---:|
| Rápido | 120 s | 8 | 24 | 2 | 2.000 |
| Equilibrado | 300 s | 16 | 28 | 3 | 4.000 |
| Detallado | 600 s | 24 | 32 | 3 | 8.000 |

Los límites no son esperas obligatorias. Rápido mantiene 3 tandas de hasta 24 referencias y 3 muestras; Equilibrado 5/32/4; Detallado 8/48/6. Los tres comparten el mismo arranque de 24 emplazamientos y dos muestras. El presupuesto agrupa ICMP, TTL y los complementos opcionales; no cuenta todos los paquetes que pueda emitir el sistema operativo.

## Resultados directos y comparación

La versión anterior se ejecutó primero en la conexión actual. La tanda nueva se ejecutó después, secuencialmente entre presets. Son condiciones observadas en momentos distintos, no un benchmark de precisión controlado; durante parte de la tanda nueva también se ejecutaron comprobaciones locales sin sondeos a Internet.

| Preset | Tiempo anterior → nuevo | Lanzamientos anteriores → nuevos | Destinos medidos anteriores → nuevos | Destinos trazados anteriores → nuevos | Contrastes nuevos |
|---|---:|---:|---:|---:|---:|
| Rápido | 26,00 → 31,48 s | 300 → 390 | 70 → 70 | 5 → 8 | 12 |
| Equilibrado | 64,93 → 95,12 s | 748 → 1.149 | 146 → 146 | 8 → 16 | 56 |
| Detallado | 124,46 → 287,04 s | 1.500 → 3.036 | 210 → 348 | 13 → 24 | 126 |

Los tres nuevos acabaron en `complete`. El Detallado anterior había agotado el límite de 1.500 lanzamientos antes de completar sus ocho tandas. Los pares son características dependientes entre sí, no 126 mediciones geográficas independientes.

| Preset nuevo | Primera alternativa | Barcelona–Meswifi | Mejor referencia Madrid/Alcobendas | Siguientes alternativas |
|---|---|---:|---:|---|
| Rápido | Cataluña | 1,6275 ms | 9,4903 ms | Comunidad de Madrid, Región de Murcia |
| Equilibrado | Cataluña | 1,5616 ms | 9,6559 ms | Comunidad de Madrid, Región de Murcia |
| Detallado | Cataluña | 1,9334 ms | 10,1437 ms | Comunidad de Madrid, Región de Murcia |

### Por qué escoge esas zonas

**Rápido:** la referencia declarada en Barcelona responde mucho antes que Madrid y el resto de referencias. Sus ocho rutas utilizables aportan doce contrastes tras detectar tramos compartidos, varios de ellos privados. Cataluña obtiene menor pérdida combinada. La topología permite distinguir dependencias, pero no sitúa geográficamente esos tramos.

**Equilibrado:** se conserva la ventaja de RTT de Barcelona y se amplía la comparación a dieciséis rutas y cincuenta y seis pares. Cataluña mantiene el primer puesto. Madrid y Murcia quedan detrás porque combinan RTT locales superiores con patrones de rutas todavía compatibles con varias salidas de red.

**Detallado:** completa 348 destinos medidos y veinticuatro rutas, con tres barridos por ruta. Barcelona sigue siendo la referencia más rápida y Cataluña encabeza el modelo combinado. Las diferencias entre caminos compartidos siguen siendo ambiguas: por sí sola, la pérdida de contrastes es menor para Madrid que para Cataluña en Equilibrado y Detallado; en Rápido es menor para Murcia que para Cataluña. No sería correcto decir que los traceroutes confirman Barcelona; la señal decisiva de estas tandas continúa siendo su RTT corto.

El cálculo usa pérdidas heurísticas, no una calibración aprendida de dispositivos españoles con ubicación conocida. En concreto, combina proximidad relativa de RTT y patrón global con pesos 0,7/0,3; cuando hay al menos tres rutas y tres contrastes utilizables, combina esa pérdida de RTT y la de contrastes con pesos 0,65/0,35. Los contrastes tienen una tolerancia mínima de 3 ms, ampliada por MAD e incertidumbre declarada de las referencias; las muestras de destinos separadas más de 180 s no se emparejan. Los ajustes ensayan varias pendientes entre distancia y RTT y un término común de acceso no negativo. **Ese término y las diferencias no se convierten en cotas físicas.** Los pesos y pendientes necesitan validación empírica adicional.

### Qué ocurre con la región del mapa

| Preset | Extensión CBG anterior → nueva, desde su centro geométrico | Barcelona satisface todas las cotas CBG nuevas |
|---|---:|---|
| Rápido | 379,6 → 366,4 km | Sí |
| Equilibrado | 366,4 → 360,6 km | Sí |
| Detallado | 357,7 → 412,5 km | Sí |

La extensión no es el error de localización ni un intervalo de confianza. Los RTT mínimos del Detallado nuevo fueron mayores, incluida la referencia de Barcelona; por ello no se obtuvo una región menor pese al aumento de mediciones. Las tandas independientes no tienen mínimos idénticos. En las tres, la región primaria permanece en `insufficient-data`: no hay coordenadas físicamente verificadas. El centro CBG no es una posición detectada del dispositivo.

## Aislar el efecto de las rutas

Para cada preset nuevo se recalculó **el mismo catálogo y exactamente las mismas muestras RTT**, suprimiendo únicamente los traceroutes:

- Cataluña sigue primera en los tres casos sin rutas. Por tanto, no se demuestra que la topología haya corregido una comunidad errónea en esta conexión.
- El orden y los pesos relativos de las alternativas secundarias cambian. Por ejemplo, en Rápido Murcia pasa por delante del País Vasco al incorporar los contrastes.
- Las regiones CBG son idénticas con y sin rutas en los tres casos. El ranking no introduce exclusiones geográficas encubiertas.
- El cambio máximo del peso relativo es 0,016857 / 0,020814 / 0,009702. Es una diferencia entre puntuaciones normalizadas, **no** una mejora porcentual de precisión ni de confianza.
- La compilación final reproduce los rankings capturados con diferencia máxima de puntuación cero. Los recorridos guardados contienen como máximo 34/51/60 respuestas por destino; el ajuste posterior del límite de validación a 192 no cambia estas entradas válidas.

## Reanálisis de la prueba móvil anterior

Se conserva el archivo original y se ejecuta el nuevo modelo offline sobre sus medidas. **Los tres presets siguen favoreciendo Comunidad de Madrid.** En Rápido solo hay un contraste utilizable y se declara el fallback; Equilibrado y Detallado tienen tres y seis respectivamente. Cataluña queda novena, duodécima y decimotercera entre las áreas medidas en ese reanálisis, sin quedar excluida de la región compatible.

Aquellas medidas daban aproximadamente 20–21 ms a Madrid/Alcobendas y 30–31 ms a Barcelona. Las extensiones CBG seguían siendo 1.503/1.710/1.706 km y Barcelona satisface las cotas. Los caminos disponibles no proporcionan una ubicación fiable del dispositivo antes de una posible salida centralizada del operador. **No se ha vuelto a medir una conexión móvil distinta ni se ha demostrado dónde está esa salida.** La causa concreta no se deduce del nombre de una IP ni de su registro administrativo.

La tanda actual con RTT bajos y la móvil anterior no deben compararse como una ganancia de precisión del algoritmo. La mejora demostrada es de recogida de rutas, detección de dependencias, explicación y conservación de incertidumbre.

## Verificación

| Límite del flujo | Resultado | Evidencia |
|---|---|---|
| Biblioteca y replay | Correcto | 177 comprobaciones: huecos, nodos privados, balanceo, duplicados, señal local frente a muchos destinos remotos, invariancia al número de saltos, límites de 192 respuestas, presupuesto parcial y región conservada |
| Ajustes → motor → contrato JSON | Correcto | 23 comprobaciones de integración web, sin sondeos a Internet |
| Integración en QuicNet | Correcto | `--test-geolocation` y `--test-geolocation-workbench` |
| Contrato → interfaz Blazor | Correcto | Navegador real: razones por zona, navegación de la zona, calidad de rutas, exportación JSON, calor, reinicio offline y pantallas de 1280/390/320 px |
| Contrato → laboratorio integrado | Correcto | Navegador real: envío de los nuevos presupuestos y límite de rutas, carga diferida, parada, mapa y ausencia de errores JavaScript |
| Compilaciones | Correcto | Biblioteca y aplicación sin errores ni advertencias; integración general con seis advertencias previas ajenas a esta modificación |

El flujo verificado es: ajustes de la interfaz → `WorkbenchSettings.ToRequest()` → motor y catálogo local → evidencia/explicación JSON → paneles y navegación del mapa. Las pruebas de navegador usan evidencia sintética identificada y teselas interceptadas; las mediciones directas anteriores son independientes de esas pruebas.

## Límites de esta implementación

Se utiliza ICMP repetido mediante .NET, **no Paris traceroute ni un solucionador TBG completo**. No se controla la identidad del flujo y no se resuelven automáticamente alias entre distintas IP del mismo router. Los barridos permiten rechazar variaciones observadas, sin garantizar que se descubran todos los caminos. Las IP privadas pueden reutilizarse y solo se tratan como evidencia débil de la sesión. La asimetría de ida/vuelta impide interpretar diferencias entre RTT de routers como retardos de enlaces.

El catálogo mantiene sus huecos de cobertura y sus coordenadas declaradas. Una comunidad sin referencias medidas no aparece como candidata directa, pero eso no la descarta. El método no está calibrado con una campaña de ubicaciones conocidas, distintos operadores y conexiones móviles españolas.

Fundamentos consultados: [TBG: delay and topology measurements](https://conferences.sigcomm.org/imc/2006/papers/p7-bassett.pdf), [artefactos y Paris traceroute](https://arxiv.org/abs/0904.2733), [pasarelas y geolocalización móvil](https://www.cse.iitd.ac.in/~aseth/ipaddresses_geolocating_cellular.pdf), [scamper de CAIDA](https://www.caida.org/catalog/software/scamper/). El importador scamper existente admite archivos locales; esta revisión no instala ni ejecuta scamper.

## Archivos y reproducción

Los datos únicos permanecen fuera de `.artifacts/`:

- Antes, conexión actual: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\presets-20261019-212214Z`.
- Después, resultados finales: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\presets-20261019-213514Z`.
- Histórico móvil indicado por el usuario: `C:\Users\Mrgaton\AppData\Local\QuicNet\GeoLatency\diagnostics\presets-20261019-204852Z`.

Cada preset nuevo conserva ajustes, catálogo/sesión en `archive.json`, revisiones, tiempo/contadores, explicación, reanálisis sin rutas y reproducción topológica. `topology-comparison.json` deriva las tablas; `provenance.json` identifica las fuentes medidas. La tanda de desarrollo `presets-20261019-213049Z` se descartó durante la revisión y no se usa como resultado final.

```powershell
dotnet run --project QuicNet.Geolocation/tests/PresetAnalysis -c Analysis
dotnet run --project QuicNet.Geolocation/tests/PresetAnalysis -c Analysis -- --topology-replay RUTA_RESULTADOS
python QuicNet.Geolocation/tools/compare_topology_presets.py RUTA_NUEVA --before RUTA_ANTERIOR --historical RUTA_MOVIL
dotnet run --project QuicNet.Geolocation.App -c Analysis -- --self-test
dotnet run --project QuicNet.Geolocation.App -c Analysis -- --self-test-web
```
