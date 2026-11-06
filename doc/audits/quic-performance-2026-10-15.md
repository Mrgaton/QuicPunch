# Auditoria de rendimiento QUIC — 2026-10-15

## Resultado

No hay un limite constante de 6 Mbps en el speedtest ni en el multiplexor de Windows. Se encontro un fallo de inicializacion del transporte que permite arrancar con CUBIC, anunciar BBR aunque el controlador no haya cambiado y aplicar ajustes despues de cuando pueden surtir efecto. En una ruta artificial con 80 ms de RTT añadido y descartando uno de cada 500 paquetes UDP por direccion, el speedtest reprodujo 9.18 Mbps de subida y 6.45 Mbps de bajada con la configuracion nativa inicial. Preparando BBR y las ventanas antes del handshake, dio 495.40/488.74 Mbps; repitiendo la configuracion corregida, 449.05/457.02 Mbps. Con el multiplexor real y el flujo completo, 410.99/477.47 Mbps.

Estas cifras son mediciones de laboratorio, no una prueba de la conexion del amigo ni del ancho de banda de la tarjeta de red. El primer contraste cambia tanto el controlador como las ventanas iniciales. La variante `--benchmark-quic-cubic` conserva las ventanas y los otros ajustes de la variante BBR para aislar el efecto del controlador. Ejecutar siempre cada variante en un proceso nuevo.

## Comparacion controlada con las mismas ventanas

Ambas variantes usan el multiplexor real, ventanas de conexion/stream de 16/4 MiB, el mismo pacing, datagramas y diez segundos de trafico simultaneo. El cambio es el controlador elegido antes de arrancar. Se ejecutaron secuencialmente en procesos nuevos y sin otras compilaciones en curso:

| Controlador | Subida Mbps | Bajada Mbps | RTT sin carga / con carga |
| --- | ---: | ---: | --- |
| CUBIC | 5.66 | 6.19 | 94.20 / 101.01 ms |
| BBR | 472.86 | 450.00 | 94.30 / 104.45 ms |

Descartes programados: CUBIC 10/5047 paquetes de subida y 10/5498 de bajada; BBR 853/426873 y 811/405662. La distinta cantidad de descartes refleja el trafico enviado bajo la misma regla de uno por cada 500, no una mayor probabilidad configurada de perdida. Este contraste reproduce el sintoma y aísla el controlador con mucha mas claridad que comparar ventanas distintas. No demuestra cual era el controlador ni la perdida real durante la sesion historica con el amigo.

## Fallos confirmados y correcciones

1. **Configuracion antes de que exista en cache.** `EnsureOptimalConfiguration()` solo modifica entradas existentes. En el servidor, las opciones concretas se crean dentro de `ConnectionOptionsCallback`, despues de los intentos de parchear la cache. El cliente generico tambien podia arrancar sin preparar su configuracion concreta. Ahora el parche de configuracion incorpora BBR, ventanas, pacing y ECN sobre el handle exacto, antes de asignarlo a la conexion.
2. **Cambio de BBR aparente.** En el MsQuic fijado por el proyecto, `QuicSettingApply` puede aceptar y almacenar otro `CongestionControlAlgorithm`, pero `QuicConnApplyNewSettings` solo inicializa el controlador si la conexion todavia no ha empezado. Leer el campo despues no demuestra un cambio real. El ajuste de conexiones activas ya no modifica ese campo; una peticion de cambiar a otro controlador devuelve false y exige reconectar. Se elimino el mensaje incondicional que anunciaba BBR activo.
3. **ECN inmutable despues de empezar.** El ajuste en bloque marcaba ECN despues del handshake. MsQuic lo rechaza y no revierte necesariamente los campos anteriores. El ajuste de ejecucion ya no incluye ECN; su seleccion pertenece a la configuracion previa. Las pruebas exigen ahora que esa llamada devuelva exito sobre la conexion preparada.
4. **Datagramas desactivados en Native AOT.** El ejecutable publicado intentaba `MethodHandle.GetFunctionPointer()` sobre el callback privado `UnmanagedCallersOnly` de .NET. El EXE AOT real falla con "no code was generated"; la inicializacion se interrumpia antes de cargar las funciones nativas y el transporte quedaba sin puente de datagramas. Ahora el MsQuic incorporado ofrece la consulta local `GetParam(0x0500FF51)` para obtener el callback ya instalado. El puente lo conserva y reenvia los eventos de .NET hasta `SHUTDOWN_COMPLETE`. No se modifica ningun paquete UDP ni el protocolo entre peers. El fallback por reflexion queda para JIT con bibliotecas del sistema.
5. **Android usaba otros valores iniciales.** El puente C no seleccionaba BBR ni las ventanas de escritorio. Ahora configura BBR, 16 MiB de ventana de conexion y 4 MiB de ventana inicial de stream en `ConfigurationOpen`, con envio almacenado y pacing. La telemetria de Android consulta tambien el controlador configurado, en vez de dejarlo desconocido.
6. **Perdida calculada con resta unsigned.** Si las perdidas espurias superaban las sospechadas, el contador se desbordaba antes de `Math.Max`. Se satura la resta en cero y el cociente en uno. Esto afecta al diagnostico y a los consumidores de la estimacion, como el ajuste de audio.

La ventana de recepcion y la ventana de congestion son limites distintos. Las cifras 16/4 MiB no implican que se vaya a alcanzar 10 Gbps en cualquier RTT; la ventana por stream y su ajuste dinamico tambien deben dimensionarse para ese producto de ancho de banda y retardo. No se desactiva congestion control ni se incrementa artificialmente el numero de paquetes iniciales.

## Recorrido revisado

- Descubrimiento, hole punching y seleccion de endpoint eligen la ruta antes del transporte de datos. Nostr se usa para señalizacion; no se encontro una retransmision de los datos del speedtest por los relays Nostr.
- El multiplexor abre un stream QUIC fisico por stream virtual. Añade un prefijo de seis bytes una vez; los bloques de datos no pasan por un unico frame global ni por el cerrojo del canal de control.
- El speedtest envia bloques de 64 KiB con cabecera y `Flush`. No hay un temporizador que limite esos bloques a 6 Mbps. En Windows se verifico `SendBufferingEnabled=true`: cada escritura no espera obligatoriamente el ACK del bloque anterior.
- El puente Android tiene copias de memoria y sondeos asincronos cada 5 ms; su `Flush` espera a los envios pendientes. Son costes adicionales y posibles limites en movil. Este trabajo no presenta esas esperas como causa demostrada del caso PC-PC ni cambia su semantica sin una comparacion especifica.
- TLS y cifrado se mantienen. Las pruebas nativas del telefono negociaron `X25519MLKEM768` y pasaron streams y datagramas con BBR en ambos extremos.

## Mediciones iniciales sin perdidas introducidas

Windows, biblioteca MsQuic incorporada, speedtest simultaneo sobre streams nativos, despues de las transferencias de calentamiento:

| RTT añadido | Subida Mbps | Bajada Mbps | RTT medido sin carga / con carga |
| --- | ---: | ---: | --- |
| 0 ms | 1247.46 | 1247.81 | 0.63 / 19.52 ms |
| 40 ms | 930.09 | 578.99 | 64.04 / 95.19 ms |
| 80 ms | 697.65 | 412.96 | 93.67 / 112.13 ms |

El proxy UDP programa el vencimiento de cada datagrama y los reenvia por trabajadores independientes, sin dormir un RTT por paquete. Comparte CPU y loopback con ambos extremos; el planificador, las colas y los buffers del sistema pueden añadir retardo o perdida adicional. El descarte programado se cuenta por separado; no es una garantia de que la perdida total del recorrido sea exactamente 0.2%. No se debe interpretar una diferencia entre ejecuciones cortas como una medicion precisa de mejora porcentual.

## PC y movil del usuario

La telemetria del PC mostro como endpoint local la interfaz de WireGuard y como remoto la direccion publica del movil. Aunque ambos dispositivos puedan estar en la misma Wi-Fi/LAN, ese recorrido concreto pasa por el tunel e Internet. Las pruebas UDP directas de la investigacion anterior tampoco respondieron; no se ha demostrado que el bloqueo sea de QuicNet, del filtro de WireGuard o de otro elemento. Se mantienen intactas las rutas y la VPN.

Las pruebas de 10 segundos con los procesos instalados se interrumpieron y no proporcionaron goodput valido. Durante el contraste aparecio una segunda instancia del PC con la misma identidad; se dejo una sola y no se usan esos intentos para atribuir una mejora al ajuste. El telefono se desconecto de ADB antes de instalar la APK corregida. Los resultados de la prueba nativa C sobre el telefono si se obtuvieron antes de esa desconexion. Cualquier resultado posterior de APK-PC debe registrarse aparte.

El resultado anterior de una prueba de solo dos segundos (132.91/31.20 Mbps desde el movil) pertenece a otra sesion: confirma que no habia un techo universal de 6 Mbps, pero no constituye una comparacion antes/despues del arreglo actual.

## Reproduccion y captura

```powershell
dotnet build QuicPunchTests -p:BaseOutputPath=.codex-perf/bin/
dotnet QuicPunchTests/.codex-perf/bin/Debug/net11.0/QuicPunchTests.dll --test-quic-optimizations --verbose
dotnet QuicPunchTests/.codex-perf/bin/Debug/net11.0/QuicPunchTests.dll --benchmark-quic-default --verbose
dotnet QuicPunchTests/.codex-perf/bin/Debug/net11.0/QuicPunchTests.dll --benchmark-quic-cubic --verbose
dotnet QuicPunchTests/.codex-perf/bin/Debug/net11.0/QuicPunchTests.dll --benchmark-quic --verbose
```

Los benchmarks son diagnosticos con trafico real en loopback durante diez segundos. No imponen un umbral de velocidad independiente del equipo. La regresion funcional verifica el controlador antes de envolver la primera conexion del servidor y comprueba que no existe un falso cambio de controlador activo.

Para investigar una conexion real, ejecutar una sola instancia por dispositivo, actualizar ambos extremos y lanzar en el PC:

```powershell
pwsh -File scripts/collect-quic-performance.ps1 -Seconds 45
```

Mientras captura, hacer una prueba de bajada, otra de subida y otra simultanea desde la aplicacion. El script solo lee la API local y guarda version, adaptadores, rutas predeterminadas y muestras de telemetria; no guarda chats, tokens ni claves. Los endpoints e identidades de peers siguen siendo informacion privada. No sumar los contadores repetidos de varias sesiones que comparten la misma conexion fisica.

Contrastar RTT sin/con carga, deltas de perdidas sospechadas y espurias, CWND durante trafico, MTU, bytes entregados y ruta efectiva. Un CWND pequeño despues de estar inactivo no demuestra un limite durante transferencia. El contador `SendRetransmittablePackets` cuenta paquetes retransmitibles enviados, no el numero de retransmisiones realizadas. Comparar tambien otra ruta autorizada o una prueba UDP/TCP externa en ambos extremos cuando se pueda: las velocidades contratadas no miden el camino P2P ni permiten identificar por si solas un policer de UDP.

## Fuentes y limites de version

- MsQuic fijado por `native-build.lock.json`: `80a065112426bce68c1da42d026478d3e40fd45e`, revisado en el checkout local. [Aplicacion de ajustes](https://github.com/microsoft/msquic/blob/80a065112426bce68c1da42d026478d3e40fd45e/src/core/connection.c), [validacion de ECN y actualizacion de campos](https://github.com/microsoft/msquic/blob/80a065112426bce68c1da42d026478d3e40fd45e/src/core/settings.c).
- [MsQuic: semantica de envio y buffers](https://github.com/microsoft/msquic/blob/main/docs/Streams.md).
- [Configuracion de System.Net.Quic](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Quic/src/System/Net/Quic/Internal/MsQuicConfiguration.cs). El proyecto compila sus publicaciones con el SDK fijado por el publicador; el codigo privado de .NET y los offsets nativos se tienen que volver a validar al cambiar esa dependencia.

Los logs locales de esta investigacion estan en `.artifacts/quic-perf-audit/` y no se publican junto con las actualizaciones.

## Validacion de la publicacion 1.1.6 (8)

Compilados Windows x64 Native AOT de archivo unico y APK Android ARM64 con las identidades de firma existentes. El EXE real, ejecutado desde una carpeta sin DLLs auxiliares, verifica BBR en el primer handshake de ambos extremos, ajuste de ejecucion, stream y datagramas bidireccionales, ademas de los 209 contratos JSON y las comprobaciones de publicacion anteriores. Pasaron las suites de optimizaciones, datagramas, multiplexor, PQC y speedtest. El puente C se compilo y ejecuto en el telefono antes de desconectarse de ADB; la instalacion de la APK nueva y el contraste completo PC-movil quedan pendientes de recuperar ese acceso.

El cambio adicional del compilador nativo elimina dos `return FALSE` inalcanzables despues de `__fastfail` en el MsQuic fijado. MSVC 19.52 los rechaza durante LTCG al tratar advertencias como errores; no se han desactivado las comprobaciones de referencia ni las advertencias globales.

Publicacion completada en `https://cdn.gato.ovh/QuicNet/`: firma RSA del manifiesto y descargas HTTPS completas de ambos artefactos verificadas. Clientes anteriores 1.1.4 (6) descubren 1.1.6 (8) para Windows y Android. En el PC queda una sola instancia 1.1.6 Native AOT, con la misma identidad y datos.

| Artefacto 1.1.6 (8) | Bytes | SHA-256 |
| --- | ---: | --- |
| Windows x64 Native AOT | 187026432 | `e16a2584c438b7855e278fca30b594a5c47d6f5a3d506bf47efc8c8e2768c16c` |
| Android ARM64 APK | 58788752 | `7a08ce753881fc73cc109e219a4d84da5dc31bf4ed336134c1e117f403c177df` |

## Continuacion con USB recuperado: prueba real en ambos dispositivos

Se instalo la APK publicada 1.1.6 (8) mediante `adb install -r`, conservando la identidad. El PC utiliza el EXE Native AOT 1.1.6 (8). Se cerro una segunda instancia Debug con la misma identidad para medir con un unico proceso. El movil conserva pantalla activa conforme a lo solicitado.

La prueba All, iniciada desde el movil, completo las tres fases de diez segundos sin errores, transfirio 1,339,752,448 bytes y quedo registrada en el historial:

| Fase (perspectiva del movil) | Subida Mbps | Bajada Mbps | RTT de sondas con trafico | Sondas sin respuesta |
| --- | ---: | ---: | ---: | ---: |
| Solo bajada | — | 439.09 | 15.11 ms | 5/46 |
| Solo subida | 106.27 | — | 8.90 ms | 0/94 |
| Simultanea | 114.35 | 412.09 | 17.95 ms | 6/33 |

Reposo: 20.46 ms, 10/10 sondas respondidas. Una prueba simultanea adicional iniciada desde el PC completo 226.07 Mbps PC→movil y 115.82 Mbps movil→PC, con RTT medio bajo carga de 41.45 ms y 3/48 sondas sin respuesta. La variacion entre pruebas confirma que estas cifras no representan la capacidad maxima de las lineas contratadas. Se mantuvo la ruta por WireGuard e Internet.

BBR aparece configurado en ambos extremos y las sondas usan datagramas QUIC reales. Las perdidas de sondas corresponden a consultas de ida y vuelta sin respuesta dentro del plazo; no equivalen al porcentaje exacto de paquetes fisicos perdidos. Los contadores nativos tambien aumentan mucho en los envios PC→movil bajo carga, por lo que queda una limitacion de ruta/colas a investigar separadamente.

La subida del movil cercana a 105 Mbps encaja con otro limite del puente Android: 64 KiB por bloque / 5 ms de sondeo del vaciado de envios = 104.86 Mbps. La comparacion con [QuicStream.FlushAsync de .NET](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Quic/src/System/Net/Quic/QuicStream.Stream.cs) muestra que la implementacion de escritorio no espera a vaciar envios en Flush. El puente Android ya copia los bytes a su cola nativa limitada en WriteAsync; esa espera adicional no es necesaria para enviar. Se prepara una comparacion cambiando solo esa semantica en Android.

Archivos de esta continuacion: `.artifacts/quic-perf-audit/mobile-all-10s/` y `pc-both-10s/`, con resultados y telemetria por segundo. No se publican sus endpoints e identidades junto al programa.


## Eliminacion del limite Android y prioridad del control (1.1.7, Build 9)

La implementacion Android de FlushAsync esperaba los envios nativos pendientes con sondeos de 5 ms. El speedtest vacia cada bloque de 64 KiB, por lo que esa espera imponia un techo practico proximo a 105 Mbps. WriteAsync ya copia los bytes a una cola nativa limitada a 4 MiB. FlushAsync ahora coincide con System.Net.Quic: valida el estado y la cancelacion, sin esperar a vaciar la red. CompleteWrites y las confirmaciones del protocolo mantienen su funcion; no se elimina la contrapresion de la cola ni la propiedad de sus buffers.

Se encontro ademas que SetQuicPriority exigia handles de System.Net.Quic antes de consultar el transporte de QuicPunch. En Android esa ruta no esta soportada y el control no recibia su prioridad solicitada. Ahora consulta primero Priority16 del transporte, que delega al puente nativo. La regresion del multiplexor verifica con un transporte sin handles .NET que tanto la escritura como la lectura de prioridad llegan a su implementacion. La suite --test-mux pasa (17.8 s).

Una primera APK que solo modificaba Flush incremento el envio, pero corto una prueba; se descarto como validacion de publicacion. Con la correccion de prioridad, una prueba All de diez segundos por fase desde el movil hacia PC Build 8 completo 439.93 Mbps de bajada, 209.61 Mbps de subida y 229.74/202.64 Mbps en simultaneo. No se afirma una mejora de la suma simultanea respecto a Build 8: la ruta y las perdidas varian.

Con ambos dispositivos en 1.1.7 (9), otra prueba All completo 1,376,452,608 bytes en 32.05 s:

| Fase (perspectiva del movil) | Subida Mbps | Bajada Mbps | RTT medio bajo carga | Sondas sin respuesta |
| --- | ---: | ---: | ---: | ---: |
| Solo bajada | — | 448.69 | 12.95 ms | 2/73 |
| Solo subida | 249.30 | — | 27.28 ms | 0/83 |
| Simultanea | 239.02 | 164.15 | 33.19 ms | 4/50 |

Reposo: 15.91 ms, 10/10 respuestas. Una prueba simultanea iniciada desde el PC completo 154.30 Mbps PC→movil y 226.54 Mbps movil→PC (10 s). Una posterior de 30 s completo 161.27 Mbps PC→movil y 235.62 Mbps movil→PC, 1,488,322,560 bytes, RTT bajo carga de 42.18 ms y 6/164 sondas sin respuesta; reposo 26.28 ms, 10/10 respuestas. Los resultados completos quedan en mobile-all-10s-both-final-9, pc-both-10s-final-9-retry y pc-both-30s-native-diag-9 dentro del directorio local de la auditoria.

Tambien se registraron fallos intermitentes durante la preparacion: una primera solicitud coincidio con la reconexion tras reiniciar el PC, una transferencia termino con error de protocolo QUIC y una prueba de 30 s se cancelo a los 14.14 s. No se cuentan como resultados correctos ni se consideran explicados por las velocidades alcanzadas. Se añadio registro permanente del status/error QUIC de cierres nativos Android y de los timeouts de heartbeat del multiplexor, sin aumentar sus plazos ni ocultar cancelaciones. La prueba posterior de 30 s con ese diagnostico completo correctamente; esto no demuestra que todos los cortes intermitentes hayan desaparecido.

La aplicacion Android fija explicitamente net11.0-android37.0, la plataforma ya usada por su compilacion, para evitar que la mezcla de SDK preview deje activos de restauracion sin version de plataforma. Se mantienen las identidades y claves existentes. Los datos de estas pruebas quedan en el historial de los dispositivos; la pantalla del movil permanece activa conforme a lo solicitado.


La prueba simultanea posterior de 60 s desde el movil tambien completo: 231.76 Mbps movil→PC y 157.47 Mbps PC→movil, 2,919,235,584 bytes, RTT 19.35 ms sin carga y 39.86 ms bajo carga; 16/324 sondas sin respuesta (4.94%), jitter 14.41 ms y ningun fallo de envio de sondas. Esta ejecucion y la de 30 s usan la APK con diagnostico nativo; los cambios adicionales son de registro de errores. Las ultimas pruebas largas no reprodujeron los cierres anteriores. Se conserva la limitacion: no se ha obtenido un codigo nativo de aquellos cierres ni demostrado que hayan desaparecido en todas las condiciones.

Tras añadir ese diagnostico, se repitio la suite --test-mux (18.8 s) y la suite --test-updates (0.2 s), que verifica firmas, alteraciones de clave/contenido, contencion de URL, hashes/tamaños, versiones crecientes, descargas atomicas, preferencias y fallo de red. La compilacion Windows sigue siendo Native AOT.


## Causa nativa del corte capturada y corregida

La repeticion con los binarios finales reprodujo el corte al comenzar la transferencia (1.33 s). El registro nativo Android mostro status 0x47 y error de transporte QUIC 0xA. Se capturo entonces ETW de Microsoft-Quic en el PC: evento ConnError 5156, **RequestedMaxAckDelay is less than TimerResolution**. La validacion del receptor cierra correctamente con PROTOCOL_VIOLATION al recibir ese ACK_FREQUENCY.

El MsQuic fijado forma RequestedMaxAckDelay con QuicConnGetAckDelay del equipo emisor y no lo acota al MinAckDelay que anuncio el receptor. Android y Windows tienen granularidades distintas. La correccion msquic-ack-frequency.patch limita por abajo el valor enviado al minimo negociado del peer, manteniendo las unidades en microsegundos. No se desactiva ni relaja la validacion de frames recibidos. Se aplica idempotentemente en NativeBuilder (Windows/Linux) y en build-native-android.sh.

Se añadio una regresion real a --test-quic-optimizations: MaxAckDelayMs local 0 y transferencia de 8 MiB con SHA-256, que obliga a respetar el minimo remoto positivo al ajustar la frecuencia. La misma prueba con el binario anterior 2e3bbb... falla con error de protocolo (0.4 s); con el nativo parcheado e997a3... pasa (0.7 s). Esto confirma la causa y la correccion independientemente de la ruta del usuario. El resultado reemplaza la conclusion provisional anterior de que faltaba explicar el error 0xA. Los cierres observados solo como cancelacion se contrastan de nuevo tras instalar ambos nativos corregidos.

La regla de [ACK_FREQUENCY](https://www.ietf.org/archive/id/draft-ietf-quic-ack-frequency-12.html#section-4) considera invalido pedir un retraso inferior a min_ack_delay anunciado por el peer. Se conserva la extension y se cumple ese limite.


## Validacion final con ACK_FREQUENCY corregido en ambos dispositivos

Tras recompilar, reinstalar la APK y reiniciar el EXE AOT, se verificaron los nativos cargados: Windows e997a3eaaf82b2176b441796e5faa0d61917395aaf8df73c8930f90315a5d5ba y Android f69eae1665ec299406512e75856772575cc5b3a932e49d930518a4d029aa9c90. La APK contiene ese mismo .so; el EXE extrae esa misma DLL. Se mantienen una sola instancia en PC e identidades/datos en ambos.

La prueba simultanea desde el PC durante 30 s completo 174.87 Mbps PC→movil y 231.00 Mbps movil→PC, 1,522,008,064 bytes, RTT de sondas 31.05 ms en reposo y 38.01 ms bajo carga, 7/161 sondas sin respuesta (4.35%). A continuacion, All desde el movil completo 1,407,057,920 bytes en 31.56 s:

| Fase final (perspectiva del movil) | Subida Mbps | Bajada Mbps | RTT medio bajo carga | Sondas sin respuesta |
| --- | ---: | ---: | ---: | ---: |
| Solo bajada | — | 458.33 | 13.93 ms | 5/42 |
| Solo subida | 237.03 | — | 32.99 ms | 0/78 |
| Simultanea | 220.57 | 209.72 | 33.66 ms | 2/63 |

Reposo en All: 9.17 ms, 10/10 respuestas. Ambas ejecuciones finalizaron sin errores y no registraron nuevos cierres nativos. Estos resultados corresponden a los archivos finales para publicar, posteriores al arreglo de ACK_FREQUENCY. No se equiparan a capacidad de LAN directa: se conserva WireGuard/Internet y sigue habiendo sondas sin respuesta bajo carga. La causa del error de protocolo se ha demostrado; no se afirma que toda la perdida del recorrido ni todos los tiempos de establecimiento inicial esten resueltos. No se ha contrastado con el PC del amigo.

Pasaron nuevamente las suites del multiplexor (19.1 s), datagramas nativos RFC 9221 (0.3 s), framing (12.7 s), optimizaciones con ACK asimetrico (0.7 s) y el smoke test del EXE AOT real. Las pruebas de actualizador tambien pasaron; sus claves y formato no cambian.


## Publicacion e instalacion completadas: 1.1.7 (9)

Publicada en https://cdn.gato.ovh/QuicNet/ tras los contrastes anteriores. Se verificaron la firma RSA del manifiesto publico y las descargas HTTPS completas de ambos artefactos, con tamaño y SHA-256 iguales a los bytes firmados. Un cliente anterior 1.1.4 (6) descubre la actualizacion firmada para ambas plataformas sin ejecutar el instalador durante esa comprobacion.

| Artefacto final 1.1.7 (9) | Bytes | SHA-256 |
| --- | ---: | --- |
| Windows x64 Native AOT, archivo unico | 187025408 | `6b3cafd27689491224133af259e2afdde08e17ce520c13ee6e9e8b3280834da6` |
| Android ARM64 APK | 62024661 | `373e3055d76962cbc9b421663167eb30d62a8b385d8ddbbab764623f8d0fc0ea` |

Ambos dispositivos del usuario confirman 1.1.7, Build 9; Android conserva paquete y certificado, y el PC mantiene un unico EXE AOT en la carpeta de instalacion de prueba. Se retiro el forward ADB temporal 15071→5071. Se conservan el tiempo de pantalla solicitado y la permanencia activa mientras esta conectado a corriente. Las publicaciones anteriores permanecen intactas; la siguiente requiere Build 10 o superior.


## Publicacion solicitada posteriormente: 1.1.8 (10)

Se recompilo el estado actual para Windows x64 Native AOT y Android ARM64 con version 1.1.8 y Build 10. La publicacion conserva las correcciones QUIC anteriores, sin cambiar el formato del protocolo ni las claves. Pasaron --test-updates (0.2 s), --test-quic-optimizations con ACK asimetrico (0.6 s) y el smoke test del EXE AOT real. El feed firmado y ambas descargas HTTPS completas se verificaron; clientes 1.1.4 (6) descubren 1.1.8 (10) en ambas plataformas.

| Artefacto 1.1.8 (10) | Bytes | SHA-256 |
| --- | ---: | --- |
| Windows x64 Native AOT | 187025408 | `ca6f9bfe2007b1dffadbd93f56a1faa36ebb6404509698e851b89813cb185757` |
| Android ARM64 APK | 62024661 | `0e9f744af514f31340194f1353b01536ce3cdaa8d791000771e6cd8f94a606a0` |

El PC actualizo de 1.1.7 (9) a 1.1.8 (10) mediante su propio actualizador: descarga, sustitucion y arranque confirmado en updates/install.log. Android descubrio la actualizacion firmada y se instalo por ADB la APK descargada y verificada, conservando datos. Ambas APIs confirman Build 10 y mantienen sus identidades. Se retiro el forward ADB temporal. La guia publica de esta version indica Build 11 o superior para la siguiente publicacion. Los archivos de 1.1.7 (9) permanecen intactos.
