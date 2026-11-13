> Actualización del 7 de noviembre: las pruebas WAN/NAT64 y la sustitución de APIs privadas de .NET se documentan en [la continuación de esta auditoría](p2p-wan-nat64-native-2026-11-07.md). Los pendientes de este documento reflejan el estado del día anterior.

# Correcciones de conectividad P2P, QUIC y medios — 6 de noviembre de 2026

El producto continúa sin TURN. Se corrigieron los fallos de implementación detallados abajo y se verificaron con nodos locales, navegador y el puente nativo en un Android ARM64 real. Las pruebas no certifican conectividad universal ni convierten la implementación en una pila ICE completa.

## Estado de los hallazgos

Los números remiten a la [auditoría inicial](p2p-quic-2026-11-06.md).

| Hallazgo | Corrección y alcance |
|---|---|
| 1. STUN | Correlación de origen, transacción, antigüedad, longitud y consumo único; el recolector reutiliza el parser validado. |
| 2. Movilidad de voz | Un cambio del endpoint QUIC conserva la dirección del socket de control. |
| 3. Ausencia de TURN | Decisión explícita: no se introduce TURN ni otro relay UDP. La garantía universal solicitada no es realizable únicamente con perforación NAT. |
| 4. IPv6 | Sockets de control y sondeo duales; direcciones IPv4/IPv6 en tokens, Hello, ACK y candidatos; XOR-MAPPED-ADDRESS IPv6; catálogos y DNS sin filtro IPv4; QUIC con pinning en ambas familias. Se conserva el puerto de host IPv6 sin mezclarlo con predicciones NAT IPv4. |
| 5. Primera llamada | Comprobación de respuesta antes de abrir la sesión, recuperación de pérdida de transporte durante la apertura inicial y reutilización de la misma solicitud. La decisión de consentimiento sobrevive al corte, se deduplica y tiene límites. Denegar o cerrar no permite reabrir reutilizando la aceptación. El nuevo handler espera la limpieza del anterior. |
| 6. Datagramas Android | Recepción nativa limitada a 256 paquetes/256 KiB y 250 ms de edad, descartando los más antiguos. Envío pendiente limitado a 256 KiB por conexión. Colas de lectura administradas reducidas. Pruebas de bytes, cantidad, edad, saturación y liberación. |
| 7. Voz | La adaptación utiliza diferencias de contadores, contempla reinicios y mide incremento del RTT sobre su base. Un RTT de 150 ms estable no provoca reducción continua; la pérdida histórica no impide recuperar bitrate. Se conserva el límite impuesto por el peor peer activo. |
| 8. VPN | Capacidad negociada de fragmentación en datagramas. Si un envío nativo se descarta, no se convierte en una cola fiable. Peers antiguos reciben feedback ICMP de MTU para paquetes DF originados localmente. La interfaz distingue Tor/stream de Datagram. |
| 9. DSCP | La llamada deja de marcar EF/46 el transporte físico compartido con transferencias y VPN. Se conserva prioridad de streams. |
| 10. Medios y control | Capacidad de pantalla v2: una unidad de vídeo/audio por stream unidireccional, con plazo de 250 ms, tamaño máximo de 2 MiB y presupuesto agregado de 4 MiB/8 envíos pendientes. Control separado. Secuencias, espera de keyframe tras pérdidas y épocas de encoder evitan entregar unidades antiguas a un decoder nuevo. Recepción limitada a cuatro unidades simultáneas. |
| 11. Gateway antiguo | Al cambiar de red se retiran leases de control y túneles y se redescubre el mapeador. Callbacks de generaciones antiguas no vuelven a publicar la ruta anterior. La vida del lease de datos se vincula al nodo, separada del plazo inicial de conexión. |
| 12. Descubrimiento/liveness | STUN reduce frecuencia con una ruta estable y tras fallos, hasta intervalos de 30 s, y reinicia el aprendizaje al cambiar de red. Reintentos con jitter. Tor usa al menos 5 s/20 s de heartbeat; el transporte nativo conserva 2 s/8 s. |
| 13. Colas de envío | MsQuic escritorio limita memoria pendiente a 256 KiB por conexión y 16 MiB globales. Se solicita CANCEL_ON_BLOCKED; se conserva cada buffer hasta su callback final. Los consumidores de datagramas salen del callback nativo. Tor mantiene 64 unidades, descarta las antiguas y no envía las que llevan 250 ms en cola. |
| 14. Configuración | Las conexiones normales parchean su configuración concreta; ajustar una conexión activa ya no reinicializa toda la caché. Los ajustes explícitos se conservan para configuraciones posteriores. Buffer inicial de stream 64 KiB, ventana 4 MiB; ventana de conexión 16 MiB; 512 streams por dirección; MTU 1248–1500, keepalive 19 s, IW10 y 0-RTT desactivado por defecto. Continúa la dependencia de la ABI y de campos privados de System.Net.Quic, ver limitaciones. |

## Arreglos adicionales encontrados al verificar Android

El listener nativo debe anunciar `AF_UNSPEC` para aceptar IPv4 e IPv6. Abrirlo con `AF_INET6` producía un rechazo ALPN en clientes IPv4 aunque el socket subyacente fuera dual. El fallo se reprodujo en el dispositivo y ambas familias pasan después del cambio.

`WritesClosed` ahora espera la confirmación nativa del cierre de envío. Antes se completaba al solicitar FIN. El aborto marca primero el estado nativo para que la liberación no espere hasta 1,5 segundos intentando vaciar un stream que ya ha caducado. El puente ARM64 recompilado está en la ruta que empaqueta el proyecto Android.

## Validación

Compilación de QuicPunchTests en configuración Test y compilación completa de QuicPunch.AndroidProbe en Test. Las últimas compilaciones Android terminaron sin errores; la de tests mantiene seis avisos de nulabilidad preexistentes. No se publicó una versión ni se reemplazó la aplicación instalada/en uso.

Pruebas dirigidas superadas (19 suites): `--test-ipv6`, `--test-session-recovery`, `--test-stun-validation`, `--test-voice-mobility`, `--test-native-audio`, `--test-screen-share`, `--test-quic-datagrams`, `--test-quic-optimizations`, `--test-connectivity-security`, `--test-security`, `--test-nat-pin-coordinator`, `--test-vpn-policy`, `--test-vpn-orchestration`, `--test-port-openers`, `--test-multiplexer`, `--test-datagram-categorization`, `--test-lan-ports`, `--test-handshake-cancellation` y `--test-nostr-trust`.

La prueba IPv6 comprueba también el endpoint real del multiplexor: el recorrido completo de descubrimiento, negociación y sesión termina en un transporte IPv6. Las pruebas de sesión cortan un túnel antes de abrir la llamada y después de responder al heartbeat, durante la espera de SessionResponse. Comprueban recuperación, consentimiento único, denegación, revocación, dos protocolos simultáneos y paquetes VPN de 2000 bytes mediante fragmentos de datagramas antes y después de reconectar.

`HomeCallBrowserTests.mjs` pasa: primera llamada desde Inicio sin visitar antes la pantalla de llamadas, deduplicación y reintento después de fallo. Audio simulado evita usar el micrófono en las pruebas de llamada/pantalla.

La regresión de pantalla detiene un consumidor de vídeo más allá del plazo de caducidad: no se acumulan callbacks abandonados y la orden de detener pantalla sigue llegando por control. La prueba de multiplexación identifica nuevos handshakes por sus GUID, sin confundir la expiración de entradas antiguas con nuevas negociaciones.

Android nativo: compilación ARM64 con `-Wall -Wextra -Werror`; cola C probada con AddressSanitizer y UndefinedBehaviorSanitizer. `qp_transport_test` ejecutado en un dispositivo real por IPv4 y por IPv6: certificado ajeno rechazado sin inutilizar el listener, certificado esperado recuperado, TLS con grupo 4588, BBR efectivo en ambos extremos, streams, datagramas, confirmación de FIN y aborto. Identidades efímeras y ejecutables de prueba retirados del teléfono al terminar. Esta ejecución es local dentro del teléfono; no equivale a una llamada completa Windows–Android a través de Internet.

Las pruebas de seguridad y catálogos STUN ahora utilizan fixtures aislados; no necesitan que un catálogo público esté disponible ni que el router real modifique sus puertos. Los registros regenerables están en `.artifacts/p2p-*.log`; binarios en `bin/Test` y `obj/Test`.

## Compatibilidad y límites

- Los formatos exclusivamente IPv4 conservan su representación. Las listas mixtas usan el bit alto del contador acotado y una longitud de dirección de 4/16 bytes. Para anuncios/tokens con IPv6 ambos extremos deben actualizarse: los lectores antiguos rechazan ese formato. No hay negociación automática de downgrade de esas listas.
- El transporte de pantalla nuevo se negocia con versión 2 y una extensión de época en Start. Con un peer antiguo se utiliza su protocolo anterior; por tanto, en esa combinación sigue existiendo el bloqueo por retransmisiones del stream de medios/control antiguo.
- Los streams nuevos pueden retransmitir dentro de su plazo; después se abortan. Esto limita la retención de unidades antiguas, no elimina la latencia física ni la congestión. Tor continúa siendo fiable y ordenado debajo de la cola de aplicación: un mensaje ya entregado a TCP no se puede retirar de esa red.
- Se excluye IPv6 link-local de los anuncios porque su scope es local a una interfaz. No se ha validado IPv6-only/NAT64, cambios Wi-Fi/móvil reales, todos los modelos de router, ni todos los escenarios de doble NAT/CGNAT. No se modificó la configuración física del equipo o del router para simular esa cobertura.
- BBR solicitado no significa BBRv3, ECN no significa L4S y XDP solicitado no demuestra que se esté usando ese datapath. No se afirma un ajuste óptimo universal sin mediciones de latencia, pérdida, CPU y memoria bajo carga en las redes objetivo.
- El runtime y la biblioteca nativa siguen fijados por el proyecto. La dependencia de detalles privados de .NET requiere repetir las comprobaciones de configuración, callbacks y buffers cuando se actualicen. Reemplazar esos hooks por una API pública propia es trabajo de arquitectura pendiente; no se presenta como resuelto por cambiar constantes.

## Por qué HTTP/3 permitido no garantiza P2P directo

HTTP/3 define semántica HTTP sobre QUIC; un firewall puede permitir esa comunicación hacia servidores concretos y bloquear UDP entre dos clientes. Un doble NAT con mapeo/filtrado dependiente del destino puede no ofrecer ningún par de candidatos directos alcanzable. IPv6, PCP/NAT-PMP/UPnP, candidatos múltiples y sondeos coordinados mejoran la probabilidad cuando existe una ruta, pero no crean permisos que un firewall niega.

Además, el protocolo de aplicación del programa es propio: anunciar ALPN `h3` no implementa por sí mismo HTTP/3. La señalización y los sondeos UDP propios también deben atravesar la red. Se respeta la prohibición de TURN sin prometer que cualquier red que permita HTTP/3 será suficiente.

Referencias primarias: [HTTP/3, RFC 9114](https://www.rfc-editor.org/rfc/rfc9114.html), [comprobaciones de conectividad ICE, RFC 8445](https://www.rfc-editor.org/rfc/rfc8445.html), [QUIC, RFC 9000](https://www.rfc-editor.org/rfc/rfc9000.html), [QUIC DATAGRAM, RFC 9221](https://www.rfc-editor.org/rfc/rfc9221.html), [QUIC_SETTINGS del commit fijado](https://raw.githubusercontent.com/microsoft/msquic/80a065112426bce68c1da42d026478d3e40fd45e/docs/api/QUIC_SETTINGS.md).
