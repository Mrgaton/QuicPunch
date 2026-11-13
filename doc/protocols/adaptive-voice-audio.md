# Audio de voz adaptativo (10 ms)

Implementado el 7 de noviembre de 2026. Sustituye la cola PCM de tres elementos por una cola de paquetes ordenada y una política de reproducción por participante.

## Funcionamiento

- Captura, paquetes Opus y mezclador: 10 ms, 480 muestras mono a 48 kHz. Los callbacks de captura parciales o agrupados se ensamblan sin truncar muestras. Los timestamps siguen el reloj de muestras; mute elimina cualquier fragmento incompleto.
- Historial móvil de llegadas: 60 segundos, máximo 6001 observaciones. Se utilizan tiempos locales monotónicos y diferencias del timestamp remoto de 16 bits; no se necesita sincronizar los relojes de ambos equipos.
- La ventana reciente de dos segundos estima el suelo del tiempo de tránsito y su percentil 95. Así un cambio permanente de ruta o una deriva de reloj no se confunde indefinidamente con jitter.
- Los excesos de más de 15 ms separados por menos de 250 ms forman un único episodio: muchos paquetes retenidos y liberados juntos no cuentan como muchas incidencias independientes.
- Tres episodios en el minuto permiten aprender recurrencia. Se usa la mediana de su severidad, no el peor paquete. Un episodio sostenido durante 500 ms también permite aumentar el objetivo.
- El objetivo empieza en 20 ms, tiene mínimo de 10 ms y máximo de 100 ms. Se evalúa cada 250 ms. Baja en pasos de 10 ms por segundo tras estabilidad; la espera de estabilidad es de 5–30 segundos según el intervalo entre episodios repetidos. En una red sin episodios puede bajar antes.
- La ocupación real converge al objetivo mediante remuestreo lineal continuo, limitado a ±2% y con cambios graduales. Hay tolerancia de fase para evitar vaciados por diferencias entre relojes. Este ajuste modifica ligeramente la velocidad y el tono: no es un algoritmo que preserve el tono.
- Se conservan los paquetes comprimidos y su secuencia hasta decodificarlos para reproducción. Un paquete desordenado que llega a tiempo conserva su contenido original.
- Cuando falta el siguiente paquete se intenta FEC del posterior si está disponible y tiene la duración apropiada; en caso contrario se usa PLC. El contador FEC mide intentos, no garantiza que el encoder incluyera redundancia.
- La compensación es acotada: tras una interrupción larga se desvanece a silencio. Un cambio sostenido de latencia puede resincronizar el decoder, con transición suave, para evitar descartar toda la voz posterior contra un plazo obsoleto.
- Se suavizan las uniones tras recuperación y reinicios, también si caen dentro de un bloque de salida. Durante una llamada se alimenta continuamente el dispositivo, incluyendo silencio.
- La capacidad máxima de recepción es 160 ms para absorber ráfagas; no es el objetivo de latencia. El exceso y los saltos enormes de secuencia se recortan con reinicio suave. Las pausas largas del mezclador descartan voz obsoleta.
- Windows conserva 60 ms de capacidad y prepara un margen de salida de 10 ms al iniciar o rellenar un dispositivo vacío. Android solicita 20 ms de buffer activo y PulseAudio solicita 20 ms con prebuffer de 10 ms; los dispositivos pueden imponer otros valores.

`NativeAudioService.GetPeerAudioStatistics` expone objetivo, audio pendiente, compensaciones, intentos FEC, paquetes tardíos/duplicados/descartados/inválidos, tamaño del historial, episodios y factor de reproducción. El objetivo y la cola no incluyen todo el retardo del dispositivo ni la latencia extremo a extremo.

## Compatibilidad

El nuevo receptor admite paquetes anteriores de 20, 40 y 60 ms y los divide en bloques de salida de 10 ms sin truncarlos. Los paquetes sin secuencia conservan el orden de llegada.

**Ambos participantes deben actualizarse para usar la nueva emisión de 10 ms.** El receptor antiguo mezcla una sola trama por tick de 20 ms y no interpreta correctamente la cadencia nueva. No se ha añadido negociación de duración con versiones antiguas.

## Validación

Suite `AdaptiveAudioBufferTests`, incluida en `--test-native-audio`, con tiempo virtual y el decoder Opus real:

| Caso | Resultado |
| --- | --- |
| Red estable | Objetivo final 10 ms; aproximadamente 16 ms pendientes; ninguna compensación |
| Jitter de 0–8 ms | Ninguna compensación ni bloque de silencio inesperado |
| Pico aislado de 80 ms | Objetivo nunca superior a 20 ms; vuelve a 10 ms; el incidente utiliza compensación |
| Picos de 40 ms cada 2 s | Aprende un objetivo de 50 ms; cero compensaciones en la fase posterior al aprendizaje; vuelve a 10 ms y unos 16 ms pendientes tras estabilidad |
| Cambio permanente de ruta de +40 ms | Resincroniza y vuelve a 10 ms de objetivo, sin quedar atrapado en el plazo antiguo |
| Pérdidas periódicas | Se ejecutan PLC/FEC sin detener la salida |
| Deriva de reloj ±200 ppm, 3 minutos por sentido | Sin compensaciones; cola acotada y sin acumular latencia |
| Reordenación y duplicados | PCM idéntico a la entrega ordenada; manejo de wrap de secuencia y timestamp |
| Historial | Caducidad a los 60 s; los episodios aislados no se multiplican por el número de paquetes de una ráfaga |
| Compatibilidad, entradas inválidas y ráfagas | Duraciones antiguas conservadas, memoria acotada y rechazo de entradas inválidas |

Se comprueban todos los saltos entre muestras, no solo los límites de bloque. En el tono de 500 Hz la mayor diferencia fue de 391 unidades PCM; en la señal con frecuencia, envolvente y armónicos variables fue de 447 tanto en limpio como con perturbaciones. Ninguna de estas trazas activas produjo un bloque de 10 ms completamente silencioso. Son métricas de continuidad de señales sintéticas, no una puntuación perceptual de voz humana.

La suite de integración comprueba captura parcial/agrupada, timestamps, mute, voz bidireccional, cambio de bitrate y recuperación tras bloquear la salida. En Windows se prueba el driver WinMM real con silencio durante 150 ticks de 10 ms: cero descartes y cero rellenos después de vaciarse durante la prueba observada. No se abre el micrófono.

Comandos:

```powershell
dotnet build QuicPunchTests/QuicPunchTests.csproj -c Test --no-restore
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-native-audio --verbose
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-opus
dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-voice-mobility
dotnet build QuicPunch.AndroidProbe/QuicPunch.AndroidProbe.csproj -c Debug --no-restore
```

No se ha realizado una llamada audible entre dos dispositivos por Internet ni una prueba física de Android/Linux. Los picos mayores que la reserva y las pérdidas reales requieren compensación; las interrupciones prolongadas pueden cortar la voz. No se afirma ausencia universal de artefactos ni latencia extremo a extremo de 10 ms.
