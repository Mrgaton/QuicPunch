# QuicNet.Geolocation.App

Aplicación de prueba de QuicNet.Geolocation 1.2: perfil, iniciar/parar y mapa en directo, con ajustes y evidencia plegados. Usa el motor jerárquico de la biblioteca; las alternativas de país, región y área local se pueden centrar en el mapa. No muestra historial ni sesiones y no graba archivos de análisis. Los catálogos y el mapa están empaquetados; los sondeos requieren conectividad.

```powershell
dotnet run --project QuicNet.Geolocation.App -c Release
```

También puedes abrir `GeoLatency.cmd`. La interfaz y el mapa base se sirven localmente. **Mapa detallado en línea** descarga teselas de OpenStreetMap solo si se activa; esa capa no envía consultas a una API de geolocalización. **Iniciar** utiliza los parámetros actuales; **Parar** cancela el trabajo. **Reiniciar** comienza un análisis nuevo en memoria.

Los perfiles ajustan el tiempo, las tandas, las muestras y las rutas del motor jerárquico. **Ajustes del análisis** permite limitar el tiempo y el número total de sondeos, seleccionar la familia IP y activar el diagnóstico de rutas/túneles, topología, Path MTU y TCP autorizado. PMTU y TCP están desactivados por defecto. **Sin red** no envía sondeos, aunque esas opciones estuvieran guardadas.

Los presets priorizan calidad: Rápido permite hasta 120 segundos y 8 destinos trazados; Equilibrado 300 segundos y 16; Detallado 600 segundos y 24. Son límites máximos, no esperas obligatorias. **Alternativas geográficas → Comparación por rutas y latencias** explica el orden de cada región. **Calidad de los traceroutes** permite revisar destinos alcanzados, saltos variables y huecos. Las rutas privadas del operador cuentan como topología de la sesión, sin asignarles coordenadas; el mapa RTT/CBG conserva su región compatible.

**Alternativas geográficas** conserva hipótesis abiertas; **Diagnóstico de la conexión** muestra interfaces, origen, MTU, rutas compartidas y mediciones complementarias. Un tiempo agotado, una cancelación o un cambio de conexión se muestran expresamente como tales. El contador de sondeos procede del presupuesto compartido del motor e incluye ICMP, traceroute, PMTU y TCP; no es un recuento de todos los paquetes que transmite el sistema operativo.

La cobertura visible se obtiene del catálogo instalado, sin asumir el antiguo nomenclátor municipal. El catálogo actual tiene 2.158 destinos, 883 grupos de emplazamiento y 267.958 localidades; incluye 101 direcciones españolas en 63 grupos. `Data/spanish-coverage.json` enumera la cobertura y los huecos por comunidad. Una etiqueta de ciudad no confirma un municipio. La web no aporta perfiles de entrenamiento: muestra su ausencia o la suspensión de comparación según la evidencia que devuelve la biblioteca.

El color representa **afinidad relativa de red**, no una probabilidad calibrada ni una ciudad confirmada. Azul indica menor afinidad; verde, amarillo y naranja indican mayor afinidad. Se calcula con señales de baja latencia, agrupando ubicaciones cercanas y conservando hipótesis separadas. Un centenar de routers lentos no puede desplazar una señal local rápida por votación. La anchura del color es una heurística, no un margen de error.

El mapa conserva por separado la afinidad dentro de cotas verificadas y la **afinidad condicional** de las coordenadas declaradas. La selección **Capa** permite inspeccionarlas; una capa sin datos se indica expresamente. **Mapa de calor** activa el color, **Referencias medidas** muestra las sondas con su RTT mínimo y **Comunidades** muestra los límites locales. **España** vuelve a la vista general; el selector permite explorar las 17 comunidades autónomas y Ceuta/Melilla. **Comparar comunidades** promedia el color de la capa sobre una malla de puntos dentro de cada territorio: su índice 0–100 es visual, no una probabilidad, clasificación del domicilio ni prueba de exclusión.

Los límites proceden de Natural Earth 1:10m; se han unido las provincias por comunidad y se empaquetan con el mapa. No son límites legales de precisión topográfica. `scripts/build-geolocation-map.py` los regenera con un GeoJSON local y Shapely (dependencia solo de preparación); `--download` descarga explícitamente la fuente. La procedencia y el hash quedan en `communitySource` del recurso. El mapa no descarga límites al iniciarse.

**Qué dicen las mediciones** explica cuántos emplazamientos respondieron y cuál fue el más rápido. Su tabla muestra, por destino, mínimo y mediana de RTT, respuestas/intentos y procedencia de las coordenadas. Las rutas pueden añadir retardos diferentes: un óptimo matemático en el mar no es una ubicación observada. Cuando solo hay coordenadas declaradas, el resultado principal dice **Ubicación sin resolver**, recupera el calor condicional y deja las coordenadas del ajuste en la inspección técnica. **Punto del modelo** está desactivado por defecto y nunca se usa para centrar automáticamente el mapa.

**Descargar resultado JSON** exporta la revisión actual, sus estadísticas por referencia y sus capas, solo a petición. No se guarda automáticamente un historial. El archivo puede contener datos sensibles de conexión; no debe dejarse como única copia en carpetas desechables.

El renderizador utiliza Canvas 2D configurado para software, con un píxel por unidad CSS. Durante los gestos copia un mapa rasterizado en caché y dibuja marcadores de tamaño fijo; simplifica y recorta la geometría y dispone los textos al finalizar el gesto. El mapa local no necesita WebGL, WebGPU ni aceleración por hardware; la capa detallada en línea es opcional. Los contadores de renderizado están en los atributos `data-*` del canvas para comprobar el comportamiento sin añadir paneles a la interfaz.

Comprobaciones de la integración actual, sin sondeos a Internet:

```powershell
dotnet run --project QuicNet.Geolocation.App -c Release -p:JsonSerializerIsReflectionEnabledByDefault=false -- --self-test-web
dotnet run --project QuicPunchTests -c Release -- --test-geolocation-workbench
```

La primera comprueba opciones, límites, cancelación con resultados parciales, alternativas del motor, serialización sin reflexión y ejecución en memoria con transporte sintético. La segunda verifica el servidor integrado, contratos JSON, CSRF, mapa local y ciclo de vida del panel.

Pruebas de navegador, con Playwright disponible mediante `QUICNET_PLAYWRIGHT_MODULE` o instalado localmente; `QUICNET_TEST_BROWSER_PATH` permite elegir un Chromium instalado:

```powershell
dotnet build QuicNet.Geolocation.App -c Release
node QuicNet.Geolocation.App/Diagnostics/WorkbenchBrowserTests.mjs
$env:QUICNET_TEST_CONFIGURATION = 'Release'
node QuicPunchTests/Tests/GeolocationWorkbenchBrowserTests.mjs
```

Comprueban las interfaces a 1280, 390 y 320 píxeles, ajustes nuevos, reinicio sin red, estados parciales, mapa y paneles de evidencia. Incluyen una regresión con un ajuste matemático en el mar: el calor condicional debe verse, las 19 regiones deben poder seleccionarse, el punto no se presenta como ubicación, se explican RTT reales de la sesión sintética y funciona la exportación JSON. La prueba Blazor carga evidencia sintética; la integrada intercepta las teselas opcionales. No demuestran precisión municipal ni disponibilidad de destinos en Internet. Las capturas y la sesión sintética se regeneran en `.artifacts/`.

La suite completa `--self-test` se ha actualizado al contrato de resultados parciales y a coordenadas declaradas sin verificación física: pasa 157 comprobaciones, incluidas regresiones CBG independientes en Barcelona, Madrid, Valencia, Sevilla y Sídney. Los resultados reales de los tres presets se recogen en `../QuicNet.Geolocation/docs/ANALISIS_ANCLAS_2026-10-19.md`.

Los contratos JSON se generan estáticamente mediante `GeoJsonContext` y `AppJsonContext`, incluidos los datos enviados al mapa.
El modificador de reflexión desactivada anterior comprueba esos contratos en `--self-test-web`; para la interfaz Blazor se vuelve a compilar con la configuración normal.

La interfaz conserva Blazor Server: `PublishAot` y `PublishTrimmed` permanecen desactivados porque este modelo de interfaz no admite Native AOT. La aplicación usa el framework declarado en su proyecto (.NET 11); la biblioteca mantiene .NET 10.

```powershell
dotnet publish QuicNet.Geolocation.App -c Release -r win-x64 --self-contained true
```

Distribuye la carpeta publicada completa, incluidos `DATA-NOTICE.txt` y `MAP-NOTICE.txt`.

Para comprobar cambios mientras otra instancia ocupa `bin/Debug` o `bin/Release`, puede usarse la configuración `MapPreview` con `-c MapPreview` y `QUICNET_TEST_CONFIGURATION=MapPreview`; utiliza las carpetas estándar `bin/` y `obj/`.
