# Tor Android: verificación del 15 de octubre de 2026

Dispositivo: OnePlus CPH2769 ARM64 conectado por ADB. La versión instalada
rechazaba el arranque con `PlatformNotSupportedException` porque el descriptor
Tor Expert Bundle solo admitía x64. El servicio Android también desactivaba Tor
y su descubrimiento al iniciar, independientemente de las preferencias guardadas.

La corrección empaqueta el ejecutable ARM64 de Guardian Project Tor Android
0.4.9.11 y lo ejecuta desde `ApplicationInfo.NativeLibraryDir`. El gestor recibe
una ruta explícita sin intentar descargar el paquete de escritorio. La extracción
de bibliotecas nativas está activada en el APK. Se restauran los ajustes guardados
de Tor y Nostr Tor, y la identidad onion existente se conserva.

Verificación realizada:

- APK Debug compilada con .NET 11 rc.1 y Android SDK instalado: cero errores;
  seis avisos existentes de disponibilidad de APIs QUIC en Android.
- Instalada por `adb install -r`, conservando los datos.
- Tor alcanzó `Active (100%) [Direct]` y creó el servicio onion.
- Nostr Tor conectado a cinco de cinco relays.
- Conexión SOCKS5 al propio servicio onion mediante un circuito Tor: respuesta
  satisfactoria. Esto verifica acceso al servicio, no una conversación de chat.
- Tras instalar el APK final y forzar un reinicio de la app, Tor volvió al 100 %
  automáticamente, sin error y con la misma dirección onion y cinco relays.
- `--test-tor-cascade`: 30 comprobaciones correctas, cero fallos. Se omite la
  prueba de ejecutable Linux ausente en Windows; el ejecutable Android se verificó
  en el dispositivo. La suite cubre la ruta empaquetada y el fallo explícito
  cuando esa ruta desaparece.

El paquete Android incluye Tor directo. No incluye los ejecutables auxiliares
Snowflake/obfs4. No se verificó chat, voz ni transferencia entre dos peers por Tor
en esta intervención.

Preparación reproducible: `QuicPunch.AndroidProbe/prepare-tor-android.ps1`.
El script comprueba SHA-256 del AAR antes de extraer el binario. Los registros
locales de esta intervención están en `.artifacts/tor-android/`.
