# Fotos de perfil

En Ajustes → Tu perfil se elige una imagen, GIF, WebP/AVIF animado o vídeo local de hasta 50 MB. El editor permite arrastrar, ampliar, girar, centrar y previsualizar el recorte. El archivo original no se sube. Se exporta un AVIF cuadrado de hasta 512 × 512 píxeles y **512 KiB (524288 bytes)**, sin ampliar recortes pequeños.

Las animaciones y vídeos permiten elegir inicio, duración de hasta 10 s y 6/8/12 FPS. Se guardan sin sonido y en bucle; la transparencia de una animación se compone sobre el color de fondo elegido. Las imágenes fijas mantienen el alfa. Las animaciones empiezan con un máximo de 320 píxeles de lado; la calidad y la resolución se reducen automáticamente si hace falta para cumplir el límite de bytes. No se guarda un archivo que supere el límite. La política se aplica también al transporte de perfiles y al almacenamiento de imágenes recibidas.

`avatar-media.js` utiliza `ImageDecoder` para conservar los fotogramas y la composición de GIF, WebP y AVIF animado, y el decodificador de vídeo del navegador para MP4/WebM y otros formatos que admita ese equipo. El navegador/WebView debe ofrecer `ImageDecoder` para las animaciones de imagen; si no puede decodificarlas se muestra un error, sin aplanarlas silenciosamente. La importación admite hasta 600 fotogramas; la exportación genera como máximo 120. No se decodifica audio. En Android se utiliza el selector nativo de documentos para imágenes y vídeos, sin permisos generales de almacenamiento.

La conversión se realiza sin conexión en `avatar-encode-worker.js`, con libavif/libaom WebAssembly incluido en `wwwroot/vendor/avif`. Se fija `speed: 2` (esfuerzo alto, con menor tiempo de codificación), calidad inicial 75, alfa 100 y YUV 4:4:4 para imágenes fijas. Las animaciones comienzan con calidad 70 y YUV 4:2:0; ambas rutas usan intentos adaptativos de calidad y tamaño hasta cumplir los 512 KiB. No depende de `canvas.toBlob(image/avif)` ni de FFmpeg, por lo que utiliza el mismo codificador en WebView2 y Android WebView. Puede cancelarse mientras comprime; guardar espera la respuesta del servidor.

El recorte se dibuja en un lienzo nuevo y se envían únicamente sus píxeles RGBA al codificador: no se copian EXIF, GPS, XMP ni el nombre del archivo. El resultado AVIF se guarda de forma atómica en el directorio `avatars` junto a `ui-settings.json`. La API `/api/profile/avatar` comprueba AVIF fijo o secuencial, dimensiones cuadradas de hasta 512 y un límite de 512 KiB; exige CSRF para guardar o quitar. `POST /api/profile/avatar/remove` elimina la foto.

La foto viaja por sesiones directas autenticadas del protocolo de chat, en mensajes `peer_avatar` / `peer_avatar_req`, sin publicar imágenes en Nostr ni añadirlas al token UDP. El receptor atribuye la imagen a la identidad autenticada de la sesión. Se sincroniza al conectar, al inicializar la interfaz con sesiones ya abiertas y al cambiar/eliminar la imagen. No se envía ni se acepta por Tor, y la interfaz conserva las iniciales del seudónimo para esos contactos. La identidad criptográfica no cambia.

`StatusApiModule` anuncia revisiones SHA-256 en el mapa `avatars`; los componentes de contactos, mensajes, participantes de llamadas y la barra lateral usan URLs locales con esa revisión. Los contratos JSON nuevos tienen metadatos generados para Native AOT.

## Contenedor animado

`avatar-avif-sequence.js` empaqueta los fotogramas AV1 independientes producidos por el codificador ya incluido. El archivo contiene la imagen de portada en `meta` y una pista de imágenes en `moov`, tiempos por fotograma y una lista de edición que indica repetición infinita. Los offsets y tamaños de las muestras se recalculan al terminar. No se añade otro códec WASM ni se depende de FFmpeg para la conversión en Android.

`AvatarAvifValidator` valida el tamaño total, dimensiones de portada y pista, duración, recuento de fotogramas, tiempos de muestras, offsets y límites de los datos antes de servirlos. Solo se acepta una pista de imágenes; no se aceptan pistas de audio. Las pruebas incluyen un AVIF animado generado realmente por el editor a partir de un GIF sintético.

## Dependencia incluida

`@jsquash/avif` 2.1.1, de https://github.com/jamsinclair/jSquash, con archivos de licencia Apache-2.0, libavif y libaom en su carpeta. Integridad del paquete npm:

```
sha512-LMRxd0fMgfCLtobDh0/sFYJMMiRJTNYSEEWvRDKXlAeZ08t3gI5V+1thIT0XjXJ+SVG7Zug9B0XPyx0Ti5VRNA==
```

Al actualizar el códec hay que conservar `speed: 2`, las licencias y los archivos WASM locales; verificar la carga bajo CSP, las dimensiones del AVIF real, el editor táctil/teclado y la cancelación. La compilación Android debe mantener `OnShowFileChooser` y el selector de documentos con tipos `image/*` y `video/*`, sin permisos generales de almacenamiento.

## Verificación

`--test-webui-security` incluye `ProfileAvatarTests`: AVIF real generado por el editor, límites, CSRF, eliminación, persistencia, transporte directo y privacidad Tor. `--test-aot-json` cubre los contratos. `node QuicPunchTests/Tests/ProfileAvatarWebTests.mjs` verifica el empaquetado de secuencias y los límites de dimensiones, duración y fotogramas. `Fixtures/profile-avatar.avif` es una imagen sintética creada para esta prueba, sin datos personales. Se verificó también la conversión y visualización real en el navegador con la CSP de la aplicación.

### Pruebas de importación y reproducción

Se comprobó el flujo real en la interfaz con GIF, MP4 y AVIF previamente animado; el navegador decodificó los fotogramas del AVIF resultante con duración y repetición infinita, y los píxeles cambiaron entre capturas de su reproducción. La prueba de contenido difícil redujo automáticamente un clip de dos segundos a 256 × 256 y 16 fotogramas, con 128868 bytes. Las fotos PNG fijas siguen exportándose a 512 × 512 cuando el original lo permite. El editor también se verificó con un ancho móvil de 390 píxeles, sin desbordamiento horizontal.

Los elementos de imagen de las listas que se reconstruyen con la telemetría se reutilizan por contacto y vista; así la animación no vuelve al principio con cada actualización de estado. La prueba web cubre esas listas, los avatares sin clave de caché y el aislamiento de Tor.
