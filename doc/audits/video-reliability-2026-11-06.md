# Revisión de videollamadas y pantalla · 6 de noviembre de 2026

Se revisaron el selector de fuentes, la captura nativa y del navegador, la detección de codificadores, los contenedores locales, los paquetes de vídeo, el transporte hacia la interfaz y la reproducción. Las correcciones tienen pruebas de regresión. Esta revisión no constituye una certificación de producción en todos los dispositivos y redes.

## Correcciones

| Problema | Resultado |
| --- | --- |
| El producto `Bitrate * BufferMs` desbordaba un entero de 32 bits con ajustes admitidos; por ejemplo, 12 Mbps y 250 ms. | El cálculo usa 64 bits y conserva el VBV solicitado, además del mínimo por fotograma. |
| La detección nativa solo codificaba al muxer `null`. | Cada candidato debe producir el contenedor real, dimensiones válidas, un fotograma clave y metadatos que pueda convertir el parser de producción. Se conserva el diagnóstico de FFmpeg cuando falta hardware o el driver falla. |
| La comprobación WebCodecs ignoraba el perfil y la descripción producidos por el codificador. | La prueba sintética conserva esos metadatos y los incluye en la negociación del receptor. Un objeto sin constructor VideoEncoder no se declara utilizable. |
| Doble inicio y cancelaciones durante selección, detección o anuncio podían dejar una captura activa o reactivarla tarde. | El inicio se serializa con una revisión de operación. Se liberan las fuentes que llegan tarde y se comprueba la cancelación tras las operaciones asíncronas. El cierre de la fuente se observa antes de detectar códecs. |
| Un error posterior al anuncio podía dejar al receptor esperando una pantalla inexistente. | La limpieza también solicita la parada al backend y conserva el error original para mostrarlo. |
| Tras rechazar un paquete por congestión podían salir deltas ya codificados que dependían del paquete perdido. | Se descarta su salida hasta enviar correctamente un fotograma clave. Una clave rechazada mantiene la espera. |
| La recuperación del decodificador podía perder avcC/hvcC, reutilizar un estado sin configurar o aceptar callbacks de una instancia antigua. | Conserva metadatos cuando se omiten, distingue su eliminación explícita, incluye geometría en la configuración, ignora callbacks antiguos y recupera errores síncronos con una nueva clave. |
| Una vista previa desconectada propagaba la excepción al codificador; una vista bloqueada podía retenerlo indefinidamente. | Los envíos binarios tienen un límite de dos segundos y abortan ese WebSocket sin propagar el fallo al emisor P2P. La exclusión mutua sigue disponible mientras terminan los envíos pendientes. |
| El verificador HTML extraía una función que ya dependía de un módulo externo. | Ahora carga y usa directamente el decodificador de producción. Existe además una suite automatizada FFmpeg → parser → paquete → WebCodecs. |

## Verificación realizada

- Compilación de `QuicPunchTests` y sus proyectos de escritorio compartidos, configuración `Test`. Se observaron seis advertencias de nulabilidad existentes en `SecurityDiscoveryTests.cs`.
- 21 suites web satisfactorias, incluidas las nuevas pruebas de cancelación y recuperación ante congestión.
- Chromium: selector, captura real desde canvas con las rutas TrackProcessor y vídeo, objetivo de 144 FPS, vista previa, visor, pantalla completa, ajustes FFmpeg, instalación y cámara. El objetivo de 144 FPS no significa rendimiento sostenido medido a esa tasa.
- 11 muestras de FFmpeg, 8 fotogramas por muestra: los 88 se decodificaron y dibujaron con dimensiones correctas. Incluyen H.264, HEVC, VP9 y AV1; implementaciones CPU y NVIDIA disponibles.
- En este equipo, `h264_nvenc` y `hevc_nvenc` funcionan. `av1_nvenc` y los tres candidatos AMF no son utilizables. Funcionan `libx264`, `libx265`, `libvpx-vp9` y `libsvtav1`.
- Windows: una ventana sintética propia, fuera del escritorio visible, valida inventario por HWND, dimensiones físicas, miniatura JPEG, captura FFmpeg real y rechazo de la fuente tras destruirla. No se capturan ventanas del usuario en esta prueba.
- Ciclo de vida FFmpeg en CPU y NVIDIA: arranque, parada, rechazo del receptor y cancelación durante negociación.
- Protocolo de pantalla por QUIC local, vista previa cerrada/bloqueada, permisos y sesiones de audio compartido, Opus, instalación FFmpeg y seguridad WebUI.

Comandos principales:

```powershell
dotnet build QuicPunchTests -c Test
dotnet run --project QuicPunchTests -c Test --no-build -- --test-ffmpeg-screen
dotnet run --project QuicPunchTests -c Test --no-build -- --test-screenshare
dotnet run --project QuicPunchTests -c Test --no-build -- --test-screen-audio
dotnet run --project QuicPunchTests -c Test --no-build -- --test-opus
dotnet run --project QuicPunchTests -c Test --no-build -- --test-webui-security
node scripts/test-web.mjs --web
node scripts/test-web.mjs --browser --filter=Screen --no-build
node scripts/test-web.mjs --browser --filter=Ffmpeg --no-build
node scripts/test-web.mjs --browser --filter=Camera --no-build
```

Los fixtures y registros regenerables están en `.artifacts/`; las compilaciones utilizan `bin/` y `obj/`.

## Límites y trabajo pendiente para validar producción

- El primer intento Android con el SDK seleccionado por el `global.json` raíz devolvió `NETSDK1147`. En la preparación posterior de 1.1.18 / build 20 se utilizó el SDK de publicación fijado, `11.0.100-rc.1.26425.128`, que sí tiene Android instalado: la APK compiló y su firma coincide con el certificado original. No se ha probado en un Android físico; ADB no detecta ningún dispositivo conectado.
- No se ha realizado una llamada entre dos dispositivos físicos, una prueba prolongada de varias horas ni una matriz de AMD/Intel, WebView2, distintas escalas DPI, ventanas protegidas, suspensión y reconexión de monitores.
- Las colas acotadas reducen retraso de aplicación, pero el vídeo usa un flujo fiable. La espera por retransmisiones y por un participante lento sigue siendo una limitación; no se ha implementado adaptación de bitrate por receptor.
- No se ha añadido sincronización audiovisual por un reloj compartido. Las pruebas de audio comprueban aislamiento de sesiones, reproducción y activación de WASAPI sin grabar audio real.
- La captura nativa de escritorio FFmpeg sigue limitada a Windows; Linux depende de la ruta del navegador. La detección correcta de un encoder no promete que cualquier ventana permita captura ni que alcance la resolución/FPS solicitados.

Referencias contrastadas para los contratos utilizados: [captura gdigrab de FFmpeg](https://ffmpeg.org/ffmpeg-devices.html#gdigrab), [estado y configuración de WebCodecs](https://www.w3.org/TR/webcodecs/), [descripción del formato HEVC](https://w3c.github.io/webcodecs/hevc_codec_registration.html).

## Verificación móvil posterior

`FfmpegDecodeBrowserTests.mjs` ejecuta ahora las 11 muestras tanto en escritorio como con viewport móvil, entrada táctil y agente Android: 88 fotogramas correctos en cada modo. Se espera el dibujo de cada fotograma antes de enviar el siguiente para medir compatibilidad sin activar el descarte deliberado por congestión durante compilaciones simultáneas. La recuperación ante congestión tiene sus pruebas independientes.

También se repitieron satisfactoriamente el visor móvil con zoom/pellizco y la reproducción real AudioWorklet con PCM sintético. La emulación usa los códecs de Chromium del ordenador: no mide consumo, aceleración MediaCodec ni compatibilidad del WebView instalado en el teléfono.

La versión 1.1.18 (build 20) se publicó en el canal estable conservando las firmas originales. `verify-published-release.ps1` verificó por HTTPS el manifiesto RSA, los tamaños y SHA-256 de los seis paquetes Windows/Android/Linux, su descompresión y la página de descarga. Las pruebas de publicación Native AOT pasaron en Windows y Linux. La reproducción en el teléfono del usuario sigue pendiente de comprobar tras actualizar.
