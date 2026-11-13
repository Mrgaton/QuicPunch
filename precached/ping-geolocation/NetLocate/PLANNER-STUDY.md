# Estudio del planificador progresivo — 18 de octubre de 2026

Este estudio documenta la versión information-v2.1. La ampliación posterior, el cronometraje fraccional y el ejecutable actual se describen en [CITY-STUDY.md](CITY-STUDY.md).

## Diagnóstico del registro original

Archivo: artifacts/live-validation/measurements.jsonl. La auditoría conserva el catálogo y modelo originales.

De 120 endpoints intentados, respondieron 115. De ellos, 58 estaban a menos de 250 km de la referencia más rápida, en Frankfurt. Se identificaron 107 ASN distintos mediante el índice local: el problema principal era la concentración geográfica, aunque la diversidad de red también ayuda a programar sondeos.

Al final, 113 de las 115 cotas estaban contenidas individualmente en otra cota más restrictiva; solo dos no lo estaban (Frankfurt atlas-7635-v4 y Tripoli atlas-7811-v4). Esto describe el estado final: una cota redundante al terminar pudo aportar información antes.

El mínimo de 23 ms no apareció hasta la ronda 9, después de 324 sondas. Con el modelo conservador original, la cota más corta todavía permitía 3622,5 km desde el landmark:

~~~text
radio = (RTT + holgura) × velocidad / 2 + incertidumbre_landmark
      = (23 + 1) × 299,792458 / 2 + 25
~~~

El área retenida era 36 586 936 km² y su radio de cobertura 3945 km. Cambiar el objetivo de discretización de 40 a 10 km y aumentar MaxCells de 12 000 a 200 000, sin cambiar las cotas, solo redujo el área un 4,37 %. Los objetivos no equivalen al tamaño real alcanzado por cada celda. El principal límite era la evidencia de red, no los píxeles.

## Cambio implementado

information-v2.1 conserva exploración mundial, dedica una cuota limitada a señales rápidas y busca referencias cuyos posibles RTT distinguirían partes diferentes de la región retenida. Evita repetir sitios, IP y recortes hipotéticos; favorece diversidad ASN obtenida localmente. Cuando hay ocho referencias a menos de 250 km de la más rápida, reduce la cuota de búsqueda cercana. Una revisión por tanda busca mejorar una cota útil o variable.

Las predicciones de RTT son tres escenarios heurísticos para ordenar sondeos, sin calibración probabilística. Se evalúan mediante 192 puntos estratificados por área, incluyendo el margen de discretización. No participan en el motor de cotas ni sustituyen mediciones. No se resta retardo de acceso, se cambia la velocidad, se descartan ramas compatibles ni se fuerza una reducción de incertidumbre.

El índice ASN incorporado se usa únicamente para selección y diagnóstico. LIVE sigue enviando solo ICMP a IP literales del catálogo. REPLAY y ANALYZE son locales.

## Comparaciones reales

Cada comparación corta utilizó 10 rondas, 12 destinos por ronda, 3 sondas por destino: **360 sondas**, 10 paquetes/s, timeout 700 ms y presupuesto 120 s. Mismo modelo, catálogo de ubicaciones y límite de celdas; ambos ejecutables tenían acceso a los metadatos ASN. La estrategia antigua ignora esos metadatos. Las ejecuciones fueron consecutivas, no simultáneas.

| Ejecución | Orden | Duración | Área final km² | Radio de cobertura km | RTT mínimo ms |
|---|---|---:|---:|---:|---:|
| legacy, par exploratorio | primera | 41,72 s | 36 586 936 | 3945 | 23 |
| information inicial, par exploratorio | segunda | 42,90 s | 22 976 977 | 3265 | 20 |
| **information-v2.1**, par final | **primera** | **44,06 s** | **13 798 943** | **2277** | **13** |
| legacy, par final | segunda | 41,82 s | 42 382 166 | 3850 | 23 |

En el par final, el área compatible fue **67,44 % menor con las mismas 360 sondas**. La nueva estrategia recibió respuesta de 98 referencias y la antigua de 106; solo una nueva estaba a menos de 250 km de su referencia más rápida, frente a 51 antiguas. No se trata simplemente de conseguir más respuestas.

Los archivos están en artifacts/planner-study/ab-legacy-1, ab-information-1, ab-information-2 y ab-legacy-2. Cada carpeta contiene measurements.jsonl, result.json, report.html y audit.json. El primer prototipo y el planificador final son distintos; la comparación final corresponde a information-v2.1.

Una ejecución adicional de information-v2.1 con **30 rondas, 1080 sondas y presupuesto 180 s** terminó con:

- 312 referencias con respuesta, 299 ASN y ocho referencias dentro de 250 km de la más rápida.
- Área de **6 331 029 km²**, radio de cobertura **1693 km**, tres cotas no contenidas individualmente en otra.
- RTT mínimo **9 ms**, alcanzado en la ronda 10; su cota permite todavía **1524 km** desde el landmark.
- Área de 7 771 448 km² tras las primeras 360 sondas; 6 331 029 km² desde la ronda 24, sin nueva reducción en las seis últimas rondas.
- Cambiar el objetivo de resolución 40→10 km con más presupuesto de celdas redujo el área un 6,36 %.

Registro: artifacts/planner-study/extended-information/measurements.jsonl. Su presupuesto es tres veces el de las comparaciones cortas; no debe presentarse como una comparación igualada a 360 sondas. El radio puede subir mientras disminuye el área por el cambio del centro y la discretización.

## Reproducir y revisar

Desde NetLocate:

~~~powershell
.\dist\win-x64\NetLocate.exe live --planner information --rounds 10 --batch 12 --samples 3 --pps 10 --timeout-ms 700 --seconds 120 --out .\experimento-information
.\dist\win-x64\NetLocate.exe live --planner legacy --rounds 10 --batch 12 --samples 3 --pps 10 --timeout-ms 700 --seconds 120 --out .\experimento-legacy
.\dist\win-x64\NetLocate.exe analyze .\experimento-information\measurements.jsonl --out .\audit.json
.\dist\win-x64\NetLocate.exe replay .\experimento-information\measurements.jsonl --out .\replay
~~~

Los nuevos JSONL congelan el ASN, versión del planificador y modelo. El esquema anterior sigue siendo reproducible: la auditoría puede enriquecer solo su diagnóstico con el índice ASN local, sin modificar cotas ni catálogo congelado.

Las 37 pruebas nativas incluyen un escenario con muchas referencias en Frankfurt y alternativas útiles en otras ciudades: con RTT fijos y el mismo presupuesto, la nueva selección reduce el área más del 20 % y conserva la posición sintética conocida. Otras pruebas comprueban que las predicciones no mutan muestras o geometría, no duplican IP y son deterministas. La aplicación existente supera además sus 116 comprobaciones sin sondeos a Internet.

## Qué no demuestra este resultado

Son varias ejecuciones desde una sola conexión. Cambios de rutas, congestión, cuantización del RTT ICMP y orden de ejecución pueden afectar a los resultados. Invertir el orden en el segundo par no equivale a un ensayo estadístico extenso.

No se introdujo la posición física real del usuario: **no se ha medido error de localización ni calibrado un intervalo del 95 %**. El mapa muestra afinidad heurística y una región compatible con el catálogo/modelo; depende de que las coordenadas declaradas sean correctas y no haya anycast o túneles no detectados. Menor área compatible no prueba por sí sola menor error real.

Una base más densa de destinos bien documentados puede ofrecer mejores oportunidades, pero muchos RTT semejantes o IP de la misma zona aportan cotas repetidas. La región restante exige evidencia nueva; no se puede resolver honestamente estrechando el color o restando retardos supuestos.

## Verificación final del distribuible

Native AOT Windows x64, .NET 10.0.13 / SDK 10.0.402. Binario de 14 806 528 bytes; PE sin cabecera CLR; 37 comprobaciones nativas superadas. SHA-256: EA9D0CA1D2F65CEC1495AECF40D80B12014F5B21C96D8DB18D3603D2E786CE49.

El replay del registro extendido con el binario final produjo result.json idéntico byte a byte: SHA-256 7039D5ABFEE408EE92A8D0CFF7E53A602ED3ED72A2445B3F730093BBC6BF0263. Se verificó también lectura del esquema 1 original.

Chromium comprobó mapas SVG, navegación de rondas, cambio visual entre rondas, vistas 1440×1100 y 390×844 sin desbordamiento horizontal, cero errores de navegador y cero solicitudes externas. Capturas en artifacts/visual-qa/live-desktop.png y live-mobile.png. Node/Playwright se utilizaron solo durante QA y no se distribuyen.
