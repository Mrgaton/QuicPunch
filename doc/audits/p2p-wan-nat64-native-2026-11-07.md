# WAN, NAT64 y eliminación de APIs privadas de .NET

## Alcance

La conexión P2P de producción usa ahora el mismo puente propio de MsQuic en Windows, Linux y Android. Se conserva el diseño sin TURN. No se ha instalado una nueva aplicación ni publicado una versión durante estas pruebas.

El puente abre y posee sus registros, configuraciones, conexiones y streams. Usa la [tabla pública de MsQuic](https://microsoft.github.io/msquic/msquicdocs/docs/api/QUIC_API_TABLE.html). Se han eliminado la reflexión sobre `_handle`, `MsQuicApi`, `MsQuicConfiguration`, la modificación de la caché de configuraciones y la sustitución de callbacks de `System.Net.Quic`. Tampoco se cambia temporalmente `APP_CONTEXT_BASE_DIRECTORY` para inicializar .NET.

Los adaptadores públicos que reciben una conexión de `System.Net.Quic` siguen siendo compatibles para streams. Las operaciones avanzadas que esa API pública no permite devuelven `false` o rechazan explícitamente `Attach`; no intentan recuperar handles privados. La ruta P2P usa `QuicPunch.QuicConnection.DatagramChannel` y conserva datagramas, prioridades, telemetría, BBR antes del handshake y pinning mutuo SHA3-256. Las pruebas y el benchmark se han migrado al puente propio.

La compilación de escritorio produce y distribuye `qp_transport.dll`/`libqp_transport.so` junto a MsQuic. El paquete AOT incluye también el puente. Android sigue compilándolo con el NDK. MsQuic/OpenSSL continúan fijados por la configuración nativa existente; esto elimina la dependencia de **internos de .NET**, no la necesidad de distribuir las bibliotecas nativas correspondientes.

## Pruebas de red

| Prueba | Topología y comprobaciones | Resultado observado |
|---|---|---|
| WAN física | Teléfono CPH2769 por datos móviles, origen privado de operador en `rmnet_data1`, hacia el PC a través de su IP pública. Mapeo UDP temporal UPnP IGD de tres minutos. Certificados de prueba fijados en ambos extremos. | TLS X25519MLKEM768, BBR, 1000 bytes íntegros por stream, FIN confirmado, 20/20 datagramas de 1000 bytes de ida y vuelta. RTT ~53 ms, MTU 1360. El listener eliminó el mapeo al finalizar. |
| NAT64, sin deterioro | Tres namespaces Linux: cliente solo IPv6 → TAYGA → servidor solo IPv4. Prefijo de laboratorio `2001:db8:64:64::/96`. Interfaz del cliente con MTU 1280. | TLS híbrido, BBR, stream/FIN y 20/20 datagramas bidireccionales. El servidor observa una dirección IPv4 traducida; el cliente observa IPv6. |
| NAT64, deterioro | La misma traducción, añadiendo 25±5 ms por sentido y 1% de pérdida configurada en la salida del cliente. | Sesión estable, RTT ~50 ms, stream íntegro y FIN confirmado. 20/20 respuestas en la primera repetición y 19/20 en la última. Los datagramas perdidos no se retransmiten. |
| Pinning nativo Linux | IPv4 y IPv6; primero se presenta un certificado intruso, después el certificado autorizado sobre el mismo listener. | Intruso rechazado; listener reutilizable. TLS 4588, BBR, prioridades, streams y datagramas correctos. |

El teléfono estaba conectado por USB únicamente para ejecutar el ayudante y recoger resultados. El tráfico QUIC usó la interfaz móvil, no ADB. Inicialmente solo se veía una Quest 2; se comprobó el modelo y se conectó el teléfono antes de efectuar la prueba WAN.

Estos resultados **no prueban todas las combinaciones de NAT**. La prueba WAN utiliza un mapeo explícito en el extremo del PC; no acredita hole punching entre dos NAT simétricas sin mapeos. NAT64 se ha probado con traducción real en un laboratorio local, no en una red NAT64 de operador. El prefijo se proporciona explícitamente al probe: esta prueba no acredita descubrimiento automático PREF64/DNS64 ni la síntesis de candidatos IPv4 literales por la aplicación. [RFC 9872](https://www.rfc-editor.org/rfc/rfc9872.html) describe las opciones de descubrimiento que requerirían validación adicional.

## Fallos encontrados y corregidos

1. **Datagramas deshabilitados en el servidor de una ruta NAT64 con MTU mínima.** La notificación inicial `DATAGRAM_STATE_CHANGED` podía preceder al callback del listener. Sin un cambio posterior de MTU, el estado quedaba en cero aunque se hubieran negociado datagramas. Al conectar se consulta `QUIC_PARAM_CONN_DATAGRAM_SEND_ENABLED` y se aplica un tamaño inicial conservador de 1150 bytes hasta recibir una actualización. El estado pendiente pertenece a la conexión que lo originó, para no mezclar handshakes concurrentes. El mismo laboratorio pasó de 0/20 a 20/20 respuestas.
2. **Carrera entre crear streams y liberar el contexto nativo.** La creación y el registro de cada stream se realizan bajo el mismo bloqueo que retira el contexto. El cierre aborta los streams antes de liberar la conexión.
3. **Espera nativa de dos segundos al abrir un stream.** El inicio se observa de forma cancelable desde C#; un límite de streams del peer se comunica como bloqueo y no inmoviliza el hilo llamante durante ese plazo.
4. **Un consumidor de datagramas podía bloquear el cierre.** La recepción nativa y los callbacks de aplicación tienen colas separadas y acotadas; los datagramas pendientes de callback caducan a los 250 ms. Se prueba el cierre mientras un callback permanece bloqueado.
5. **Diferencias innecesarias entre Android y escritorio.** Ambos usan el mismo codec de categorías/fragmentos. Se evita decodificar sobres y reconstruir identidades en el transporte físico cuando los datagramas los consume exclusivamente el multiplexer. Se mantiene `DGRAM_PRIORITY | CANCEL_ON_BLOCKED` y la memoria pendiente acotada.
6. **Compilaciones simultáneas competían por la salida nativa compartida.** NativeBuilder mantiene un bloqueo de archivo durante la preparación, compilación y copia de artefactos; las siguientes compilaciones esperan hasta su límite de tiempo.

La prueba de vídeo categorizado asumía que una única ráfaga de 32 KiB por datagramas debía llegar completa. Con una ventana de congestión fría puede descartarse parte de la ráfaga. Ahora comprueba recuperación mediante nuevos keyframes con identificadores distintos; no exige retransmitir el frame perdido. En la ejecución observada llegó el tercer frame, aproximadamente 220 ms después del primero. La pantalla de la aplicación conserva sus streams unidireccionales independientes con caducidad, comprobados por su prueba específica.

## Reproducción y manejo de artefactos

- `QuicPunch.AndroidProbe/native/qp_network_probe.c`: cliente/servidor independiente para la prueba de transporte; recibe identidad propia y certificado público esperado. No contiene claves ni destinos de producción.
- `scripts/test-nat64-lab.sh <probe> <directorio-de-identidades>`: crea y retira namespaces, TAYGA y netem. Requiere Linux/WSL con `iproute2` y `tayga`. No cambia rutas ni firewall del namespace del host.
- `--test-wan-listener`: prueba WAN explícita; requiere `QUICNET_TEST_IDENTITY_DIRECTORY` fuera del repositorio y usa un mapeo temporal que retira en `finally`. No pertenece a la batería offline.
- Los certificados efímeros se generaron fuera del repositorio y de las carpetas de compilación. No se utilizaron claves de publicación. Las copias de Windows y Linux se han eliminado. El teléfono se desconectó de ADB: queda pendiente retirar su directorio de prueba `/data/local/tmp/qp-wan-20261107`, que contiene el ayudante, dos bibliotecas, `client.pfx` y `server.der`. La identidad es exclusiva de esta prueba y no autoriza acceso a la aplicación.
- Una limpieza concurrente eliminó `.artifacts` y `bin/Test` durante la validación. Se regeneraron los binarios y se utilizó una copia temporal externa del host de pruebas; esto no alteró los cambios fuente. Los registros son regenerables y el presente documento conserva los resultados relevantes.

## Validación final

- Batería completa: **18 suites, 0 fallos**. Datagramas nativos, pantalla, recuperación de sesiones, IPv6, movilidad de voz, audio, seguridad de conectividad, seguridad general, multiplexer, políticas y orquestación VPN, cancelación de handshakes, STUN, coordinación de NAT, apertura de puertos, categorías de datagramas, puertos LAN y confianza Nostr.
- Compilaciones `Test` de escritorio/pruebas y Android: correctas. Se conservan seis avisos de nulabilidad en las pruebas de seguridad y seis avisos CA1416 en el probe Android legado. Puente nativo recompilado para Windows, Linux y Android ARM64.
- Colas nativas comprobadas con AddressSanitizer y UndefinedBehaviorSanitizer. Repetición final de NAT64 con el último puente: correcta en ambos escenarios; namespaces eliminados al terminar.
- Comprobación de publicación migrada al puente y ejecutada desde las pruebas: primer handshake con BBR, ajustes, streams y datagramas bidireccionales correctos. **No se ha publicado ni ejecutado un nuevo binario Native AOT**: esta ejecución verifica esa lógica en el host de pruebas.
- Benchmark BBR con multiplexer: 479,51 Mbit/s de subida y 478,20 de bajada; RTT medio sin carga 91,09 ms y con carga 93,09 ms. Circuito artificial local con RTT configurado de 80 ms y descarte de un paquete de cada 500 en cada sentido; no mide capacidad WAN ni demuestra superioridad frente a otros controladores. Corregido el proxy del benchmark para tolerar el ICMP de puerto cerrado que Windows comunica como `ConnectionReset` cuando termina el endpoint.
- Una primera ejecución de seguridad agotó el plazo de interrogación previo a QUIC; dos ejecuciones completas posteriores pasaron sin cambios de código para ese caso. No se ha demostrado la causa de ese fallo transitorio.
- La búsqueda final no encuentra los accesos privados de .NET eliminados. La prueba WAN física precedió a los últimos ajustes de cierre, callbacks y compilación; estos sí quedaron cubiertos por la batería local y NAT64. No se ha repetido WAN con el teléfono desconectado.
