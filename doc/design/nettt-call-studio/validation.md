# Validación de la migración nativa

Fecha: 7 de noviembre de 2026. Versión publicada: 1.1.20, compilación 22, canal estable.

Se verificó la firma del manifiesto público y se descargaron íntegramente el EXE,
el APK y el archivo de fuentes desde el CDN para comprobar sus hashes.

- Windows: `fdf093769cb21378e9776dd13717f81a313eceb8fc0fe678872035387ac12d1e`.
- Android: `642f8218f5663efa953a7bef237f6028900680898035617c0b8759111e9fd155`.
- Descargas: https://cdn.gato.ovh/QuicNet/

## Comprobaciones realizadas

- Windows: decodificación de muestras reales H.264, HEVC, VP9 y AV1 por software.
- Windows: H.264, HEVC y VP9 con fotogramas D3D11 y presentación nativa, incluidos cambios de tamaño. AV1 por hardware no está disponible en este equipo y se anuncia como tal.
- Motor administrado: planificación por PTS, búfer, software forzado, ocultación/parada, paquetes inválidos y recuperación tras fallar la liberación de una superficie durante la negociación.
- Captura: generación sintética H.264, HEVC y AV1, encapsulación de configuración real y decodificación con las bibliotecas FFmpeg incluidas. Pruebas del codificador Windows y de sus argumentos.
- 21 suites rápidas de interfaz y 54 suites de navegador verificadas. La prueba de descarga se repitió aisladamente después de una colisión de compilación con el compresor de publicación.
- Prototipo: perfiles independientes, parámetros, validación, persistencia, aplicación/reversión, compatibilidad, teclado y disposición adaptable.
- Ejecutable AOT aislado: cuatro códecs desde las bibliotecas empaquetadas, QUIC, contratos JSON, preferencias, interfaz y API de actualización.
- Android Release: compilación, identidad y firma original, integridad de las cuatro bibliotecas multimedia y ausencia del generador de imágenes Debug. La actividad sintética no se incluye en el manifiesto Release.

## Límites de estas comprobaciones

La versión final de Android no se ha ejecutado en un dispositivo físico autorizado.
No usar las Quest del usuario. Las pruebas de navegador simulan la API nativa;
no constituyen evidencia de presentación en Android. Los resultados de la prueba
sintética tampoco garantizan un FPS concreto a otra resolución o perfil.

No se ha medido latencia extremo a extremo ni consumo CPU/GPU de una llamada
entre dos equipos con esta versión. El tiempo de procesamiento mostrado por el
visor corresponde al último paquete, no a la latencia total. El búfer de película
no implementa sincronización audiovisual completa.

La publicación usa `scripts/publish-updates.ps1`: comprueba los paquetes y sus
firmas, sube archivos por versión, verifica hashes y sustituye el manifiesto al
final. Las claves originales permanecen fuera del repositorio y del CDN.

## Revisión de ajustes y caché: 1.1.21 (23)

Publicada en el canal estable el 7 de noviembre de 2026. Se verificaron la firma
del manifiesto y los hashes de las descargas públicas completas de Windows,
Android y las fuentes nativas.

- Detección local compartida entre llamadas y lectores concurrentes; consultas
  remotas resueltas desde el inventario sin abrir decodificadores ni superficies.
- Matriz exclusiva de ajustes de vídeo con Hardware ★ y Software ✓.
- Selector fijo de cuatro formatos, botones GPU/CPU y navegación por teclado;
  sin expansión al pasar el ratón ni selector redundante del motor de captura.
- Presets y resumen efectivo dentro de «Afinar la codificación».
- Prueba nativa de caché (16 lectores concurrentes y 100 consultas inmediatas),
  21 suites rápidas y ocho suites de navegador relacionadas superadas.
- Ejecutable AOT aislado y APK firmado verificados; no se usaron las Quest.

Windows SHA256: `b8b147a8dcc72ae08d68a937e6ec1b1a16d0c3cd027b72c06576795c96a983f8`.
Android SHA256: `ad38290d19d65955373140e9bc19da9d0cb9eeb890d73f3a24e4ae9bda31895a`.
