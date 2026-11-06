# Pantalla Android y cámara

En una llamada, **Compartir pantalla** abre el consentimiento de MediaProjection de Android. Android 14+ permite elegir una aplicación o toda la pantalla. La captura mantiene una notificación con **Dejar de compartir**, usa un servicio `mediaProjection` y no reutiliza permisos entre sesiones. La revocación del sistema o el final de la llamada libera proyección, pantalla virtual, superficie y codificador. La rotación conserva la actividad; Android adapta la región capturada a la superficie del codificador manteniendo la proporción.

MediaCodec produce H264 sobre una Surface, sin leer píxeles por JavaScript ni instalar FFmpeg. La salida Annex B se convierte a NAL con longitud de cuatro bytes y descriptor AVC, usando los mismos paquetes, comprobación del decodificador, consentimiento por contacto y transporte QUIC existentes. La vista previa recibe esos paquetes por el WebSocket local. La cola existente limita el retraso y espera al siguiente fotograma clave cuando descarta fotogramas. Se desactivan B-frames y se solicita un fotograma clave cada segundo. El perfil inicial limita la captura a 1280 × 1280 y hasta 60 FPS. La pantalla incluye vídeo; el audio continúa por el micrófono de la llamada, sin capturar el audio interno de otras aplicaciones.

**Activar cámara** funciona en Windows/WebView2 y Android/WebView con `getUserMedia`, únicamente vídeo. El micrófono sigue perteneciendo al backend de llamadas. Se reutiliza WebCodecs con 720p, 30 FPS y negociación del códec; el selector muestra los dispositivos tras conceder permiso. Android incluye un control frontal/trasera. Los permisos WebView solo se conceden al origen local de QuicNet y requieren permiso Android. La cámara se utiliza con la aplicación visible.

Hay una transmisión de vídeo por emisor: cambiar entre cámara y pantalla detiene y libera la anterior. El byte opcional 11 del mensaje de inicio identifica cámara (`1`); la ausencia identifica pantalla. Los clientes antiguos pueden reproducirla como una pantalla. Los paquetes de vídeo no cambian.

## Verificación

- Compilar el proyecto Android real para verificar manifiesto y wrappers Java, además del proyecto de pruebas de escritorio.
- `dotnet QuicPunchTests/bin/Test/net11.0/QuicPunchTests.dll --test-platform-video`: SPS/PPS, configuración AVC, NAL, entradas malformadas y fotograma H264 sintético real. Genera `.artifacts/android-video-fixture.json` para la prueba de navegador.
- `--test-screenshare`: transmisión QUIC, incluida la identificación de cámara.
- `--test-aot-json`: contratos compartidos sin reflexión.
- `node QuicPunchTests/Tests/CameraBrowserTests.mjs`: cámara sintética en escritorio/móvil, dispositivos, frontal/trasera, permisos denegados, vista previa, selección del camino MediaProjection y limpieza. Nunca accede a cámaras ni pantallas reales. La prueba H264 indica explícitamente si Chromium carece de decodificador; `QUICNET_REQUIRE_H264=1` exige esa prueba.
- `node QuicPunchTests/Tests/ScreenCaptureBrowserTests.mjs`: regresión del capturador WebCodecs existente.

La autorización MediaProjection, los codificadores del fabricante y la reproducción entre un teléfono físico y Windows requieren una prueba en esos dispositivos. Una compilación o una cámara sintética no verifican ese recorrido físico.

Referencia de permisos y ciclo de vida: [Android MediaProjection](https://developer.android.com/media/grow/media-projection).
