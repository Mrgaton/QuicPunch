# Publicar y mantener las actualizaciones de QuicNet

La distribución pública está en https://cdn.gato.ovh/QuicNet/. La carpeta correspondiente es Z:\CDN\QuicNet. Si el servidor tiene CDN dentro de otra carpeta, pasa la ruta real con -CdnRoot; la URL pública debe seguir siendo /QuicNet/.

## Publicador automático en Windows y Linux

Con PowerShell 7, `./scripts/publish-updates.ps1` compila el escritorio del sistema actual y Android, incrementa versión/Build, firma y publica. Busca automáticamente `CDN/QuickPunch`, `CDN/QuicPunch` o `CDN/QuicNet` en unidades Windows locales, externas y de red, y en puntos de montaje Linux detectados con findmnt (incluidos NFS), /mnt, /media y /run/media. La búsqueda es limitada, sin recorrer todos los archivos. Si encuentra más de una carpeta exige `-CdnRoot`; nunca elige arbitrariamente entre servidores. Conserva la ruta pública `/QuicNet/` por compatibilidad con clientes instalados.

```powershell
# Windows + Android, incremento automático y descubrimiento del CDN
./scripts/publish-updates.ps1
# Añadir Linux, compilado y probado dentro de Ubuntu en WSL
./scripts/publish-updates.ps1 -WithLinux
# Preparar sin escribir en el CDN; publicar luego los mismos bytes
./scripts/publish-updates.ps1 -Version 1.1.12 -Build 14 -WithLinux -LocalOnly
./scripts/publish-updates.ps1 -Version 1.1.12 -Build 14 -WithLinux -SkipBuild
```

En Linux también puedes ejecutar `bash scripts/publish-updates.sh`, con los mismos parámetros, por ejemplo `-Channel beta`. Requiere .NET SDK 11 fijado, PowerShell 7, clang/gcc, cmake, make, nasm, perl y las dependencias de MsQuic descritas en el workflow nativo; para Android, el workload y SDK/JDK. `ANDROID_HOME`, `JAVA_HOME`, `-AndroidSdkDirectory` y `-JavaSdkDirectory` permiten elegir las instalaciones. `-AndroidKeyStore` debe apuntar al mismo keystore de los dispositivos existentes; no crees una identidad nueva. Las claves de publicación permanecen fuera del CDN.

Native AOT requiere el sistema de destino: Linux compila Linux y Android; Windows compila Windows y Android. Para publicar las tres plataformas desde Linux, importa la carpeta generada por el builder Windows con `-WindowsArtifact /ruta/desktop`; desde Windows se admite `-LinuxArtifact /ruta/linux` o `-WithLinux`. Cada carpeta contiene el binario probado y `build-info.json`; se comprueban plataforma, versión, Build, canal y SHA-256 antes de firmar. `-Platforms linux-x64,android-arm64` permite seleccionar explícitamente los destinos. No se presentan binarios JIT como AOT ni se mezclan Builds de equipos distintos. En WSL se usa una copia de fuentes y caché nativa persistentes en el disco Linux para evitar la lentitud de NTFS/NFS.

También se puede separar la preparación de la copia al servidor. Tras ejecutar `publish-updates.ps1 -LocalOnly`, `publish-prepared-updates.ps1 -Distribution /ruta/cdn -CdnRoot /montaje/cdn/QuicNet` publica los mismos paquetes y el mismo manifiesto firmado desde Windows o Linux. No recompila, no requiere las claves privadas ni el SDK Android en el equipo que copia. Verifica la clave pública de los clientes, las rutas, tamaños y hashes de todos los artefactos, conserva las versiones inmutables y reemplaza el manifiesto al final mediante un renombrado. `-Channel beta` selecciona el manifiesto beta. Es útil si una unidad NFS funciona desde Linux pero el cliente de Windows no responde; la copia debe ejecutarse como el usuario con permisos de escritura en el servidor. `scripts/test-prepared-publisher.ps1` comprueba alteraciones, rutas, versiones y fallos sin tocar el CDN.

## Zstandard y página de descarga

Desde 1.1.12 (Build 14), cada plataforma incluye un `.zst`, comprimido con nivel **19**, ventana máxima **27 (128 MiB)** y búsqueda de coincidencias a larga distancia. Windows y Linux usan un binario Native AOT; Android conserva su APK firmada. El manifiesto mantiene los campos anteriores y añade `compressed: {algorithm, url, sha256, size, windowLog}` dentro de cada artefacto. Su firma cubre ambos conjuntos de hashes y tamaños. Se conservan los archivos normales para que los clientes anteriores puedan actualizarse; los clientes nuevos prefieren Zstandard. No se modifica el funcionamiento de Estable/Beta.

El actualizador valida la firma y la URL HTTPS, limita el tamaño y ventana, verifica el SHA-256 comprimido, descomprime a un temporal con límite de salida y verifica de nuevo tamaño/SHA-256 antes de instalar. Un fallo no expone un archivo parcial al instalador. Windows y Linux mantienen sustitución, confirmación de arranque y restauración; Linux conserva el permiso de ejecución. Android sigue requiriendo la confirmación de instalación del sistema.

La raíz del CDN contiene `index.html`: permite elegir Android, Windows o Linux y Estable/Beta. Beta elige la compilación más reciente entre pruebas y releases. Usa WebCrypto para la firma y hashes y un worker WebAssembly para descomprimir en el dispositivo antes de guardar el EXE/APK/BIN; no necesita una API ni dependencias de terceros durante la visita. El decodificador zstddec y sus licencias se alojan en el mismo CDN. El uso desde móvil y PC se comprueba con `DownloadPageBrowserTests.mjs`.

Los recursos del portal se publican en `download-assets/<versión-build-hash>/` para no recibir JavaScript antiguo por la caché inmutable del CDN. Si Cloudflare responde a los canales con `cf-mitigated: challenge`, la página ofrece «Verificar navegador» y «Reintentar»: la comprobación se completa manualmente en una pestaña del servidor, nunca se intenta resolver o eludir desde el script. La verificación automática de la publicación comprueba por HTTPS las firmas y los seis archivos; las pruebas del navegador usan además un servidor aislado para comprobar la descompresión completa y este caso de protección.

Linux x64 requiere glibc y las bibliotecas gráficas GTK 3 y WebKitGTK 4.1 para la ventana Photino; sin ventana se conserva la interfaz en el navegador. En Ubuntu 24.04: `sudo apt install libgtk-3-0t64 libwebkit2gtk-4.1-0`. Tras descargar el BIN, ejecuta `chmod +x QuicNet-linux-x64.bin` y ábrelo. La captura nativa específica de Windows no se ofrece como captura Linux; se utiliza la del navegador.

## Canales estable y beta

En Ajustes → Actualizaciones puedes elegir **Estable** o **Beta**. La elección se guarda en `updates/channel.txt`, independientemente de la instalación automática. Las instalaciones nuevas de una compilación beta usan Beta por defecto; las estables y las instalaciones anteriores continúan en Estable. Una elección guardada tiene prioridad al actualizar.

- Estable consulta `latest.json`, compatible con los clientes anteriores.
- Beta consulta `latest-beta.json` y `latest.json`, y elige el Build mayor de ambos. Una beta llega solo a los usuarios de Beta; una release estable llega a Estable y Beta si es más reciente que su instalación. Ante un mismo Build se prefiere la estable. Publicar beta nunca reemplaza `latest.json`.
- El canal forma parte del contenido firmado y se valida contra el feed de origen. Los manifiestos antiguos sin canal se consideran estables. Si un feed falla, Beta puede usar una publicación válida del otro; nunca instala un manifiesto con firma o canal incorrectos.
- La suscripción a Beta se guarda incluso sin tocar el selector y se conserva al instalar una release estable. Así esos dispositivos recibirán también las siguientes betas. `CurrentChannel` describe la compilación instalada, `Channel` la suscripción y `LatestChannel` la actualización ofrecida.
- Cambiar de canal descarta cualquier paquete pendiente del canal anterior y comprueba el nuevo. Durante una descarga o instalación no se permite cambiarlo.
- No hay degradaciones: al volver de Beta a Estable, se conserva la versión instalada hasta que haya una estable con Build superior. Los números Build siguen aumentando entre ambos canales y plataformas.
- Las descargas e instalaciones automáticas respetan la preferencia de cada dispositivo. Una nueva release puede sustituir una beta pendiente de instalar; si falla la comprobación, se conserva cualquier descarga ya verificada.

Para preparar una prueba, elige un Build superior al de **los dos** manifiestos publicados:

~~~powershell
./scripts/publish-updates.ps1 -Version 1.1.11 -Build 13 -Channel beta -LocalOnly
./scripts/publish-updates.ps1 -Version 1.1.11 -Build 13 -Channel beta -SkipBuild -CdnRoot 'Z:\CDN\QuicNet'
~~~

Los archivos beta quedan en `.artifacts/releases/1.1.11-13-beta/cdn`, con artefactos bajo `releases/1.1.11-13-beta/`. La APK muestra `1.1.11-beta`; la identidad de paquete y las claves se conservan. El publicador verifica también el canal del EXE y la APK al reutilizar archivos con `-SkipBuild`.

Para publicar después una estable, recompila con un Build superior y `-Channel stable` (también es el valor predeterminado):

~~~powershell
./scripts/publish-updates.ps1 -Version 1.1.11 -Build 14 -Channel stable -LocalOnly
./scripts/publish-updates.ps1 -Version 1.1.11 -Build 14 -Channel stable -SkipBuild -CdnRoot 'Z:\CDN\QuicNet'
~~~

En compilaciones manuales, `-p:ReleaseChannel=beta` marca el canal. `Release.props` conserva `stable` por defecto; publicar una prueba no cambia ese valor. No copies el manifiesto beta sobre el estable para promocionar una versión: recompila y publica una estable con Build nuevo. Los clientes antiguos necesitan actualizarse primero a una versión que incluya el selector para poder optar al canal beta.

## Historial de distribución

La versión 1.1.16 (Build 18), estable, distribuye una nueva compilación del código actual para Windows x64 y Linux x64 Native AOT y Android ARM64. Conserva las identidades de firma, el canal estable y los paquetes normales y Zstandard verificados mediante el manifiesto firmado.

La versión 1.1.15 (Build 17), estable, reúne la actualización de IP y puerto tras verificar anuncios firmados, el aislamiento de contactos WAN/Tor, el cierre de conversaciones conservando su historial, la corrección de la previsualización FFmpeg y el visor de pantalla completa con controles superpuestos y zoom. Renueva el selector de presencia y sus SVG. El laboratorio de localización añade un catálogo GeoNames local con pueblos y municipios, ajuste de varias referencias de latencia, localidades candidatas, procedencia y discrepancias explícitas; no presenta el municipio como confirmado. Conserva las identidades de firma y distribuye Windows x64 y Linux x64 Native AOT y Android ARM64.

La versión 1.1.12 (Build 14), estable, añade Tor y su descubrimiento apagados por defecto, categorías de Ajustes con Mi perfil inicial, ubicación local opcional de contactos y RTT en sus tarjetas. Publica Windows x64 y Linux x64 Native AOT y Android ARM64 con la misma identidad Android. Incorpora descarga Zstandard, página de descargas con verificación local y el publicador con detección de discos/montajes. La primera compilación Linux verifica MsQuic/OpenSSL PQC, BBR y datagramas reales, SQLite, API y recursos web desde un único ejecutable.

La versión 1.1.11 (Build 13) reúne la interfaz adaptable, ajustes instantáneos de inicio/segundo plano y notificaciones, timbre recurrente, mejoras de chats y selector de captura, correcciones de audio y WebCodecs, gráficas de red y actualizaciones separadas en Estable/Beta. Estado y control del actualizador comparten WebSocket; los errores web llegan a la consola nativa con contexto y trazas. FFmpeg pasa a ser una dependencia opcional instalada por separado. Windows conserva la distribución Native AOT y Android conserva el paquete y certificado existentes.

La primera publicación conjunta es 1.1.1 (Build 3), con Windows x64 Native AOT y Android ARM64. La APK inicial Build 2 puede descubrirla con el mismo feed y la misma clave de firma. La versión 1.1.2 (Build 4) incorpora FFmpeg al EXE AOT, corrige la salida de audio Windows y añade el selector visual a los ajustes de captura. La preparación 1.1.3 (Build 5) añade seudónimos y saneamiento de certificados Tor, separación de identidad en chat/Watch y bloqueo de alternativas WAN para Tor. La publicación 1.1.4 (Build 6) reúne esas mejoras y corrige la cancelación de sondas del speedtest, conserva el historial de pruebas interrumpidas y utiliza la media de subida y bajada en el indicador simultáneo. La publicación 1.1.5 (Build 7) corrige los anuncios propios de Android mediante metadatos JSON generados: la APK desactiva la reflexión y antes podía escuchar a otros sin publicar su propia identidad. Los errores de creación del anuncio quedan en el registro. La publicación 1.1.6 (Build 8) prepara BBR antes del handshake en Windows y Android, corrige ECN aplicado demasiado tarde y restaura el puente de datagramas del EXE Native AOT. La prueba de publicación exige un handshake real con BBR y datagramas bidireccionales dentro del EXE AOT. La publicacion 1.1.7 (Build 9) elimina la espera artificial de Flush en Android, conserva la prioridad nativa de los streams de control y corrige ACK_FREQUENCY para respetar el minimo anunciado por el receptor y evitar PROTOCOL_VIOLATION entre Windows y Android. La publicacion 1.1.8 (Build 10) distribuye nuevamente el codigo actual con las correcciones QUIC verificadas. La publicación 1.1.9 (Build 11) incorpora nombre editable, fotos de perfil AVIF cuadradas con recorte y codificación de máximo esfuerzo, controles WAN/Tor en Inicio, filtros de contactos con contadores y ajustes de audio/vídeo solo durante las llamadas. La publicación 1.1.10 (Build 12) añade avatares animados con esfuerzo AVIF 2 y límite de 320 KiB, separa el puerto LAN de los candidatos WAN, conserva hasta ocho puertos explícitos y corrige la búsqueda de puertos y las reconexiones Android. Cambia la cabecera UDP a PNci: ambos extremos deben actualizarse. Las siguientes versiones deben usar Build 13 o superior. Windows conserva los datos y una copia del EXE anterior; Android mantiene el certificado de la APK instalada.
## Instrucciones para agentes

1. Lee este documento y conserva los cambios existentes del usuario. No cambies la identidad del paquete Android (org.quicpunch.androidprobe), su clave de firma ni la clave pública del actualizador.
2. Comprueba Release.props y ambos manifiestos publicados. Si se solicita una prueba, usa `-Channel beta`; reserva `-Channel stable` para una publicación definitiva. Elige una versión X.Y.Z y un Build entero MAYOR que todos los anteriores de ambos canales. Windows y Android comparten Build; Android lo usa como versionCode.
3. El publicador usa el SDK 11.0.100-rc.1.26425.128 para mantener las herramientas Android disponibles; puedes seleccionar otro con -DotNetSdkVersion cuando tenga los workloads necesarios. Ejecuta las pruebas de las funciones modificadas y la suite --test-updates. Instala las herramientas C++ de Native AOT, .NET 11, Android SDK/JDK y los nativos QUIC del proyecto si faltan. No omitas un error de compilación ni lo ocultes convirtiendo el PC a JIT.
4. Desde PowerShell 7, en la raíz del repositorio:

~~~powershell
./scripts/publish-updates.ps1 -Version 1.1.11 -Build 13 -LocalOnly
~~~

El script compila el PC en Native AOT, incorpora las DLL nativas, sin incluir FFmpeg, prueba el EXE sin archivos auxiliares y compila/firma una APK ARM64. Después genera latest.json firmado con hashes y tamaños de los dos archivos. Guarda todo en .artifacts/releases/1.1.11-13/cdn. Revisa los resultados antes de publicar.

5. Para publicar esa misma compilación:

~~~powershell
./scripts/publish-updates.ps1 -Version 1.1.11 -Build 13 -SkipBuild -CdnRoot 'Z:\CDN\QuicNet'
~~~

SkipBuild reutiliza los artefactos, verifica su identidad, genera el manifiesto y sube ambos archivos antes de cambiar latest.json mediante renombrado. No actualices latest.json a mano ni publiques un EXE de bin/Debug. Una versión publicada es inmutable: para corregirla incrementa Build. Si Z: no responde, conserva la distribución local y vuelve a ejecutar esta orden cuando esté disponible.

6. Comprueba desde fuera de la red local que latest.json y ambos archivos se descargan por HTTPS y coinciden con los hashes firmados. Prueba una actualización real en un PC y móvil de prueba. El script verifica las copias del disco del servidor; eso no confirma su exposición HTTP.

## Claves y continuidad de Android

La versión 1.1.14 (Build 16) integra el motor de geolocalización y el laboratorio de Ajustes → Localización: análisis automático por IP con caché persistente y un mínimo de cinco minutos entre ejecuciones, pruebas manuales coordinadas, registros y mapa cargados únicamente al abrir el panel, y capa opcional de municipios y calles. Conserva las firmas originales y utiliza sus copias permanentes fuera de las carpetas de compilación. Incluye Windows x64 y Linux x64 Native AOT, y Android ARM64.

El cliente verifica una firma RSA-3072/SHA-256 del contenido exacto del manifiesto. La clave pública está en QuicNet.Shared/Updates/release-public.pem y se incorpora a ambos clientes. La clave privada se guarda fuera del repositorio y del CDN: `%LOCALAPPDATA%/QuicNet/signing/release-private.pem` en Windows; en Linux, `QuicNet/signing/release-private.pem` dentro de la carpeta de datos del usuario (`$XDG_DATA_HOME`, normalmente `~/.local/share`). `Get-DefaultReleaseSigningKeyPath` en `scripts/ReleaseSecrets.psm1` devuelve la ruta exacta. Puedes pasar `-SigningKey` con otra ruta permanente de esa misma clave. El publicador rechaza claves y keystores dentro del repositorio, del CDN o de carpetas `artifacts`/`.artifacts`, así como rutas que atraviesen enlaces.

Haz una copia de seguridad privada: sin esa clave los clientes instalados no aceptarán nuevas publicaciones. No regeneres la clave para resolver una clave perdida. Una rotación requiere un mecanismo de transición firmado por la clave anterior. Si recuperas una copia de la antigua clave, que se guardaba en `.artifacts/update-signing/private.pem`, trasládala a la ubicación permanente y comprueba que corresponde a `release-public.pem` antes de publicar. No sobrescribas una clave permanente existente sin verificarla.

`.artifacts/` está reservada exclusivamente para archivos temporales y regenerables, incluidos los paquetes preparados para publicar. No contiene identidades, credenciales ni datos únicos. `./scripts/clean-project.ps1 -WhatIf` muestra la limpieza prevista; sin `-WhatIf` también elimina `.artifacts/`. Guarda fuera de esa carpeta cualquier resultado que quieras conservar. La limpieza se detiene para esa carpeta si detecta la antigua clave de firma, para permitir su traslado antes de borrarla.

Los scripts de publicación usan `bin/updates/` como salida intermedia de compilación. No crean carpetas `.codex-*` en cada proyecto. La limpieza elimina las carpetas antiguas de ese tipo cuando solo contienen `bin/` y/o `obj/`, además de `.codex-build/`, las carpetas `bin/obj` y las cachés de Visual Studio. Conserva `.codex/` y cualquier carpeta `.codex-*` cuyo contenido no reconozca como compilación.

Las APK actuales conservan la identidad del keystore de desarrollo original. Su copia permanente se guarda en `%LOCALAPPDATA%/QuicNet/signing/android-release.keystore`, junto a la clave del manifiesto, fuera del repositorio, del CDN y de las cachés. `Get-DefaultAndroidSigningKeyStorePath` devuelve esa ruta en la carpeta de datos del usuario. El publicador la utiliza por defecto cuando existe; para instalaciones todavía no migradas busca el keystore original en `%LOCALAPPDATA%/Xamarin/Mono for Android/debug.keystore` o `~/.android/debug.keystore`. Copiar el archivo no cambia la identidad: se comprueba el SHA-256 del certificado contra `QuicPunch.AndroidProbe/signing-certificate.sha256`. No generes otro keystore ni cambies el certificado de una instalación existente: Android rechazará la actualización.

La carpeta de firma y los archivos privados tienen permisos restringidos al propietario, SYSTEM y Administradores en Windows. Las copias privadas verificadas están en `%USERPROFILE%/.quicnet-release-backup/`, igualmente restringidas y fuera de las carpetas de limpieza. Incluyen la misma clave RSA y el mismo keystore; no son identidades nuevas. Conserva estas copias como datos privados y no las añadas al control de versiones ni al CDN.

Para un keystore personalizado usa -AndroidKeyStore y -AndroidKeyAlias. Las contraseñas se suministran por QUICNET_ANDROID_STORE_PASS y QUICNET_ANDROID_KEY_PASS, nunca se suben al CDN. Si no se indican, se usan las credenciales estándar del keystore de desarrollo. Guarda también una copia privada de ese keystore.

## Formato y seguridad

Cada manifiesto contiene payload y signature en Base64. payload es JSON UTF-8 con schema:1, channel (`stable` o `beta`), version, build, publishedAt y artifacts.win-x64 / artifacts.android-arm64. Cada artefacto tiene url relativa, sha256 y size. La firma cubre los bytes exactos de payload, sin reserializarlos. Los manifiestos existentes sin channel siguen siendo válidos para Estable.

Los clientes comprueban al abrir (tras 5 segundos) y cada 15 minutos. Ante un error reintentan cada minuto. Las comprobaciones manuales no requieren reiniciar. Los clientes rechazan firmas incorrectas, archivos alterados o demasiado grandes, redirecciones HTTP, rutas fuera de /QuicNet/ y Build antiguos. El SHA-256 de una descarga debe coincidir antes de exponerla al instalador. HTTPS conserva su validación normal de certificados; no se desactiva.

El CDN actual devuelve un año de caché inmutable. El cliente añade ?check= seguido de la ventana UTC de un minuto al pedir latest.json para no quedarse en un manifiesto antiguo. Si administras las reglas del servidor, también puedes excluir latest.json de la caché. No se envía Cache-Control: no-cache en la solicitud porque las pruebas contra este CDN activaban un desafío Cloudflare con ese encabezado. Los artefactos de releases/ son inmutables y sí pueden almacenarse en caché.

La configuración automática se puede desactivar en Ajustes → Actualizaciones. Se guarda en la carpeta de datos del nodo, updates/automatic.txt. La instalación conserva los datos, contactos y ajustes.

La interfaz recibe el estado y progreso del actualizador por el mismo WebSocket que el estado general, sin polling HTTP. Comprobar, instalar, cambiar de canal y activar o desactivar la instalación automática son comandos de esa conexión. Al reconectar recibe una instantánea completa; los cambios de progreso se agrupan y llevan revisión para descartar respuestas antiguas. La consulta del manifiesto y la descarga del CDN siguen usando HTTPS con los intervalos indicados arriba.

Windows x64: la distribución es un solo QuicNet.exe AOT. Extrae bibliotecas a %LOCALAPPDATA%/QuicNet/native/<hash>; no requiere que el usuario distribuya DLLs. Un proceso auxiliar espera a que termine el original, vuelve a verificar la descarga, sustituye el EXE, lo reinicia y espera una confirmación de arranque. Conserva QuicNet.exe.previous y restaura la versión anterior si el arranque falla. Si una instalación falla, marca esa descarga con .failed para evitar un bucle de reinstalaciones automáticas; una publicación nueva puede instalarse y el botón Instalar permite reintentar expresamente. El registro queda en updates/install.log. La carpeta del EXE debe permitir escritura al usuario; si no, la actualización falla conservando el original. Las versiones de desarrollo JIT permiten consultar/descargar pero indican que se necesita la distribución AOT para la sustitución automática. WebView2 Runtime sigue siendo necesario para la ventana de Windows.

Android ARM64: descarga automática y verificada. Android requiere permitir instalar desde esta aplicación y confirmar la instalación del paquete; una APK normal no puede omitir ese diálogo. El botón Instalar abre los ajustes de permiso si hace falta y continúa al regresar. En segundo plano queda preparada hasta abrir la aplicación. El sistema comprueba también el certificado de la APK. Las llamadas activas aplazan la instalación en ambas plataformas.

## Recuperación

Si una publicación tiene un problema funcional, distribuye la corrección con un Build mayor. Para volver al código de una versión anterior, recompílalo con un Build nuevo; los clientes no se degradan por modificar latest.json. En PC también puedes cerrar QuicNet y restaurar manualmente QuicNet.exe.previous.

Las aplicaciones ya instaladas sin este actualizador necesitan instalar manualmente una primera versión con el sistema nuevo. A partir de esa versión podrán descubrir las siguientes.

## JSON y Native AOT

Los DTOs con nombre conservan las propiedades de los mensajes anteriores. Los atributos JsonSerializable en los contextos generan los metadatos durante la compilación; las llamadas al serializador usan esos contextos o las opciones cuyo TypeInfoResolver los referencia. Para añadir mensajes, declara su DTO y regístralo en el contexto de su componente. No añadas objetos anónimos que dependan de fabricar convertidores durante la ejecución.

La prueba --test-aot-json compara los bytes con System.Text.Json y prueba la lectura de vuelta. También verifica que el almacén de contactos/ACLs mantenga el formato y lea archivos antiguos. El EXE publicado ejecuta estas comprobaciones con --release-smoke-test, comprueba sus bibliotecas nativas, HTTP, WebSocket y cambios de ajustes sobre datos temporales. El publicador exige que pase, también con -SkipBuild.

El número de publicación y la versión del protocolo son conceptos distintos. Compilar en AOT conserva el formato de comunicación. Si se cambia ese formato de forma incompatible, actualiza explícitamente la versión de cabecera del protocolo y las pruebas de rechazo de versiones distintas.

### FFmpeg como dependencia opcional

Las nuevas compilaciones no incorporan `ffmpeg.exe`, ni en el EXE Native AOT ni en Android. El publicador no necesita una instalación de FFmpeg. Solo se incluyen las DLL nativas de la aplicación. La prueba AOT rechaza recursos FFmpeg incluidos y permite ejecutar la aplicación sin FFmpeg instalado.

En Audio y vídeo se comprueba que una instalación externa responde a `-version`. Si falta, se muestran comandos para copiar, enlaces de instalación y «Ya lo he instalado · comprobar». Los comandos nunca se ejecutan desde el servidor. Windows recomienda `winget install --id yt-dlp.FFmpeg --exact --source winget` y ofrece Gyan.FFmpeg como alternativa. Se detectan las instalaciones WinGet de usuario/sistema y el PATH actualizado, incluso si Explorer conserva el anterior. También se admite `QUICNET_FFMPEG_PATH` y una instalación junto al EXE.

Linux utiliza `/etc/os-release` (o `/usr/lib/os-release`) para identificar ID e ID_LIKE: Debian/Ubuntu usa apt; Arch y derivados, pacman; Fedora, ffmpeg-free mediante dnf y un enlace a RPM Fusion para codecs adicionales. Fedora Atomic y distribuciones desconocidas reciben instrucciones sin un comando supuesto. Esta guía no añade captura nativa Linux: esa ruta sigue requiriendo Windows; Linux puede utilizar la captura del navegador. Android no sondea FFmpeg ni muestra su instalación.

Verificación: `dotnet run --project QuicPunchTests -- --test-ffmpeg-installation`, `--test-aot-json`, `--test-updates`, y las pruebas web de instalación, captura y ajustes. Validar además el EXE AOT desde una carpeta sin FFmpeg; si falta, la guía debe ser accesible y el resto de la aplicación debe arrancar.
