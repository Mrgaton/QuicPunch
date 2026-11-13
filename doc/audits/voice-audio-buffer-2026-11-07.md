# Investigación del petardeo de voz — 2026-11-07

La hipótesis de un margen de audio demasiado pequeño es consistente con el código. Hay mecanismos reproducidos que pueden causar discontinuidades, pero no se ha capturado una llamada afectada ni confirmado su causa acústica concreta. La investigación no modifica el código de producción.

## Hallazgos

- Las tramas son de 20 ms, 960 muestras mono a 48 kHz (`QuicPunch/Helpers/OpusVoiceCodec.cs:24`).
- El commit `93299bc`, del 6 de noviembre, redujo la cola PCM por participante de seis tramas (120 ms) a tres (60 ms); los buffers de salida WinMM pasaron de ocho a tres y la cola de captura de dieciséis a tres. Son límites de capacidad, no retardos garantizados. El mismo commit contiene correcciones válidas de WinMM que no conviene revertir en bloque.
- No hay una reserva inicial ni un objetivo adaptativo de ocupación. El mezclador consume la primera trama en el siguiente tick de 20 ms (`NativeAudioService.cs:294,326`). Tener capacidad para 60 ms no equivale a mantener 60 ms de protección frente a variaciones de llegada.
- Si no hay muestras de ningún participante, el mezclador no llama a `PlaySamples` (`NativeAudioService.cs:347`). Si queda audio de otros participantes, el participante sin muestras desaparece de la mezcla. No se genera PLC al vencer el plazo de reproducción de una trama ausente.
- La compensación de pérdidas (PLC) se ejecuta al llegar un paquete posterior que revela un salto de secuencia (`NativeAudioService.cs:447`). Por eso puede llegar después del hueco de salida. La cola puede además desechar parte del PLC y del audio útil durante una ráfaga.
- La llamada cola de jitter almacena PCM ya decodificado. Se decodifica en orden de recepción, sin esperar una ventana de reordenación. El timestamp recibido se ignora (`NativeAudioService.cs:425`) y un paquete anterior al último decodificado se descarta (`:443`). Más capacidad PCM no corrige esto.
- El encoder solicita FEC (`OpusVoiceCodec.cs:55`), pero la recepción normal nunca pide decodificar la FEC del paquete siguiente (`NativeAudioService.cs:459`). Activar FEC en el encoder no garantiza que cada paquete contenga redundancia.
- Windows descarta silenciosamente una nueva trama si sus tres buffers están ocupados (`WindowsWaveAudioBackend.cs:438`). A la inversa, tener tres buffers asignados tampoco asegura mantener audio por adelantado en el dispositivo. El reloj del mezclador y el consumo físico no se sincronizan mediante una medida de ocupación.
- Android solicita 40 ms de buffer activo; PulseAudio solicita 40 ms con 20 ms de prebuffer. Estas capas se suman a la cola de recepción, pero capacidad solicitada no es latencia real medida.
- `GetPeerBufferedAudioMs` mide únicamente la cola PCM, sin audio pendiente en el dispositivo ni tiempos de captura, codificación o transporte. No es latencia extremo a extremo.

## Comprobaciones ejecutadas

Se compiló un ejecutable de diagnóstico con los archivos actuales de `QuicPunch/Audio/*.cs` y `OpusVoiceCodec.cs`, la biblioteca Concentus disponible y una salida simulada. No se abrió micrófono ni altavoz. Fuentes y resultados regenerables: `.artifacts/audio-investigation/`. Compilación en sus carpetas estándar `bin/` y `obj/`.

1. Paquetes Opus en orden `1,2,4,3,5`: cinco tramas de salida, una de ellas PLC, y un paquete ignorado. El paquete 3 se pierde lógicamente aunque llegue antes de reproducir la secuencia: el decoder ya avanzó al 4.
2. Cuatro paquetes de 20 ms encolados antes de consumir: se conservan 60 de los 80 ms recibidos. Se descarta audio aunque la red no haya perdido ningún paquete.
3. Servicio y mezclador reales, backend simulado: tras reproducir la primera trama se esperaron 75 ms sin entregar el siguiente paquete. Hubo cero escrituras de sustitución durante la espera. En la segunda ejecución el intervalo entre dos bloques de 20 ms fue de 78,3 ms. Es una medida de llamadas al backend simulado, no de silencio acústico del hardware.

También se ejecutó un modelo determinista de la política FIFO: 500 paquetes de 20 ms durante 10 s; desde el segundo 1, una pausa de entrega cada segundo, seguida por liberación de los paquetes en orden; ticks exactos y sin pérdida de red. El margen adicional retrasa el inicio de consumo. Se cuentan ticks sin muestras, excluyendo la espera inicial intencionada.

| Pausa de entrega | Margen adicional | Capacidad | Ticks sin muestras | Tramas descartadas |
| --- | ---: | ---: | ---: | ---: |
| 20 ms | 0 ms | 60 ms | 1 | 0 |
| 20 ms | 20 ms | 60 ms | 0 | 0 |
| 40 ms | 0 ms | 60 ms | 2 | 0 |
| 40 ms | 20 ms | 60 ms | 1 | 0 |
| 40 ms | 40 ms | 60 ms | 0 | 0 |
| 80 ms | 0 ms | 60 ms | 20 | 18 |
| 80 ms | 40 ms | 60 ms | 18 | 18 |
| 80 ms | 40 ms | 120 ms | 2 | 0 |

El modelo ilustra el compromiso, no mide calidad de voz ni predice una red real. Al no haber PLC de reproducción, después de un hueco el FIFO puede acumular más retraso y estabilizarse: no toda variación pequeña implica petardeo permanente. La capacidad de 60 ms impide absorber algunas ráfagas mayores y vuelve recurrentes los descartes. Los paquetes pendientes al final de la ventana no se cuentan como perdidos.

## Propuesta razonada

1. Mantener las tramas de 20 ms. Ensayar una reserva adicional de 40 ms (dos tramas), comparándola con 20 ms. En condiciones regulares, esperar tres tramas antes de comenzar deja dos en reserva tras consumir la primera. Es un punto de partida experimental, no un valor óptimo demostrado.
2. Separar esa reserva del límite de seguridad: probar 120 ms de capacidad máxima para absorber ráfagas, sin esperar a llenar esa capacidad ni mantenerla como objetivo. Recortar solo cuando realmente se acumula audio obsoleto.
3. Implementar la espera por secuencia antes de decodificar Opus. Declarar perdida una trama cuando vence su plazo de reproducción, no en cuanto llega la siguiente fuera de orden. Descartar paquetes que ya perdieron su turno y no reproducir más tarde la trama sustituida.
4. Producir audio continuo durante voz activa: usar FEC cuando esté disponible a tiempo, en su defecto PLC acotada; pasar a silencio suave/reacumulación durante interrupciones largas o silencio explícito. No mantener PLC indefinidamente durante mute. Si se usa FEC, la duración pedida al decoder debe ser la duración ausente (960 muestras para 20 ms), no el buffer de 2880 muestras usado como capacidad en el helper actual.
5. Verificar también la reserva de salida física. Un buffer de red no protege por sí solo ante un mezclador que se retrasa y deja vacío WinMM. Evitar sumar reservas grandes en todas las capas sin medir su ocupación.
6. Medir por participante: cola actual/objetivo, intervalos de llegada, paquetes reordenados/tardíos, PLC/FEC, descartes por límite; y por salida: escrituras rechazadas, vaciados del dispositivo y retrasos del mezclador. Registrar también saturación de muestras para distinguir clipping de cortes.

Validación pendiente: comparación A/B en los dispositivos afectados con red estable, jitter de 10–40 ms, ráfagas de 60–100 ms, pérdidas/reordenación, carga de CPU, mute/unmute y llamadas largas (deriva de relojes). Comparar discontinuidades y latencia real, no solo el contador de cola. Las pruebas actuales verifican orden, reloj, sonido bidireccional y recuperación de bloqueos, pero no demuestran continuidad acústica bajo jitter.

## Referencias primarias

- [WebRTC NetEq](https://webrtc.googlesource.com/src/+/refs/heads/main/modules/audio_coding/neteq/g3doc/index.md): separa llegada y reproducción, adapta el retraso, considera deriva de relojes y genera PLC cuando falta audio en el instante de reproducción.
- [RFC 7587, sección 3.3](https://www.rfc-editor.org/rfc/rfc7587.html#section-3.3): la FEC puede recuperar el paquete anterior usando información del siguiente; depende de que el encoder la incluya y el receptor pueda utilizarla.
- [API del decoder Opus](https://opus-codec.org/docs/opus_api-1.5/group__opus__decoder.html): el tamaño solicitado para PLC/FEC debe corresponder a la duración ausente.
