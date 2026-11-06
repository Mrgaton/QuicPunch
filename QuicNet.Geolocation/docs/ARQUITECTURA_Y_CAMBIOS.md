# Arquitectura y decisiones de implementación

## Entrada y ejecución

`ProgressiveLocator.Start` mantiene la API original y deriva por defecto a `EnhancedLocationEngine`. `UseHierarchicalEngine=false` conserva el planificador heredado para compatibilidad. `AdditionalCatalog`, `FingerprintProfiles` y `Metadata` se suministran localmente. Ni el constructor ni el modo offline descubren la IP pública o consultan servicios remotos.

El motor selecciona un lote de direcciones literales, consulta rutas locales y distribuye muestras por pasadas intercaladas. Cada sublote tiene como máximo `Parallelism` operaciones. Las tareas se consumen individualmente y se vacían antes de publicar para que un error en el callback no abandone sondeos activos. El límite compartido se adquiere antes de ICMP, TTL, PMTU y TCP. Un lanzamiento TCP puede producir varios paquetes y retransmisiones: `SentPackets` conserva el nombre heredado, pero en el motor nuevo representa intentos de sondeo lanzados.

Las observaciones se consolidan por referencia, con hasta 100 muestras. Al alcanzar ese límite se conservan el mejor RTT y las muestras recientes. Cancelaciones y presupuestos completan con resultados parciales; las excepciones de programación o callbacks siguen propagándose. Los cambios de interfaz/origen en un destino repetido detienen la agregación para no mezclar conexiones.

## Capas de inferencia

1. `LatencyEstimator` y `EvidenceReconciler`: restricciones físicas, con los criterios de confianza originales. Una declaración no se convierte en una ubicación comprobada solo por responder al ping.
2. `DeclaredLandmarkRefiner`: escenario condicionado a coordenadas declaradas. Cuando no existen cotas primarias, esta región puede alimentar un ajuste explícitamente condicionado; no reemplaza la región principal.
3. `NetworkFingerprintEstimator`: perfiles empíricos voluntarios, diferencias de RTT en las mismas dimensiones, pérdida Huber, discrepancias ordinales, metadatos del operador y una contribución limitada de routers comunes. Los perfiles envejecen a 180 días y no se aplican entre familias IP.
4. `HierarchicalLocationPlanner`: conserva varias hipótesis. Reserva exploración global, reparte sondeos entre ramas y usa variación esperada de perfiles o una aproximación geométrica solo para ordenar mediciones. Las predicciones del planificador nunca se incorporan como observaciones.
5. `AccessTopologyAnalyzer`: observa routers compartidos, saltos coincidentes consecutivos y divergencias. Saltos sin respuesta no equivalen a rutas coincidentes. Un punto de agregación no es la dirección del usuario.

## Calidad de las huellas

Mínimo: cuatro emplazamientos, dos muestras de tiempo compatibles por emplazamiento y posición real consentida. Se separan perfiles por IPv4/IPv6 y método de cronometraje. La normalización resta el mínimo sobre la intersección de referencias tanto en el perfil como en la muestra. Esto elimina un componente común, no los retardos específicos de cada ruta. Los pesos normalizados son similitud, no probabilidad posterior calibrada.

La ponderación por ASN/tecnología es una heurística acotada; requiere datos reales para demostrar mejoras. No hay gradient boosting ni red neuronal presentada como entrenada. La información aportada por grupos de muestras repetidas se limita con `DatasetGroup`.

## Detección de túneles

Windows: consulta `GetBestRoute2`, identifica interfaz y MTU, y clasifica señales locales de túnel. Unix: selección local de origen con socket UDP sin `Send`; no equivale necesariamente a la política de enrutamiento ICMP. Adaptadores identificables más rutas efectivas aportan más evidencia que el MTU aislado.

`PathMtuProbe`: primero control pequeño, después tamaños grandes y búsqueda acotada. DF se activa en IPv4. `PacketTooBig` limita la cota superior; un timeout repetido con control exitoso sigue siendo indeterminado. 1472 es la carga ICMPv4 sobre una MTU IP 1500 con cabeceras normales, no una constante universal de Internet. IPv6 dispone de un resultado de capacidad explícito sin atribuir semántica DF portable inexistente.

Estados: `Indeterminate`, `NoIndicators`, `PossibleEncapsulation`, `LocalTunnelIdentified`. `AbsenceGuaranteed` siempre es falso. No se manipulan rutas ni adaptadores, no se fuerza tráfico fuera de VPN y no hay API de reputación.

## Catálogos y rendimiento

Recursos comprimidos, índices por teselas de un grado y cachés acotadas heredadas. Deduplicación por sitio físico aproximado y dirección. Directorio `Data/` contiene una semilla operativa limitada; el generador permite importar snapshots extensos en tiempo de preparación. Su actividad de red no forma parte del runtime .NET. Los hashes del manifest son de integridad, no firmas de confianza ni prueba de precisión.

Los catálogos de IP/ASN/router ausentes son opcionales. No se sustituye una base de prefijos completa por rangos inventados. El código acepta `OfflineMetadata` para rangos documentados por el integrador y no usa nombres DNS inversos en tiempo de ejecución.

## Cambios transversales

- `IcmpTransport` utiliza cronometraje envolvente por defecto y Workbench lo solicita explícitamente.
- `NetworkProbe` admite presupuesto compartido y devuelve datos parciales.
- El nivel `RouteCorroborated` no se concede solo por un router localizado en una base.
- Workbench registra la cancelación parcial devuelta por el motor.
- Fuentes JSON generadas para modelos añadidos; escrituras atómicas y límite de entrada de 128 MiB.
- .NET 10 y proyecto de consola separado. No se mantiene una declaración de compatibilidad AOT sin haberla comprobado.

## Fuentes primarias utilizadas para diseño y datos

- RIPE Atlas: https://atlas.ripe.net/docs/apis/rest-api-manual/anchors/
- Servicios medibles de anchors: https://atlas.ripe.net/docs/howtos/anchors/anchor-services
- Registros públicos: https://atlas.ripe.net/api/v2/anchors/
- GeoNames, estructura y licencia: https://download.geonames.org/export/dump/readme.txt
- Windows GetBestRoute2: https://learn.microsoft.com/en-us/windows/win32/api/netioapi/nf-netioapi-getbestroute2
- MIB_IPFORWARD_ROW2: https://learn.microsoft.com/en-us/windows/win32/api/netioapi/ns-netioapi-mib_ipforward_row2
- PMTU IPv4: https://www.rfc-editor.org/rfc/rfc1191
- PMTU y pérdidas ambiguas: https://www.rfc-editor.org/rfc/rfc8899
- Herramienta scamper: https://www.caida.org/catalog/software/scamper/

Estas referencias sustentan técnicas o formatos; no certifican los resultados de esta implementación.

## Distinción entre infraestructura y localidades

Las etiquetas `ANCHOR_CITY_LABEL` de la semilla identifican emplazamientos de infraestructura, no puntos municipales verificados. `LocalityCatalog.Near` las excluye de las candidaturas y de la coincidencia con localidades publicadas. El generador de GeoNames permite añadir puntos geográficos reales sin confundirlos con referencias medibles. La captura de huellas rechaza sesiones sintéticas y conserva la fecha de las mediciones originales al importar una sesión, en lugar de rejuvenecerla con la fecha de importación.
