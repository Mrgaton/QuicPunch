# Implementación de la limpieza de código

Fecha: 11 de octubre de 2026. Continuación de la [investigación y plan](dead-code-and-complexity-2026-10-11.md).

## Resultado y criterio

Se implementaron las eliminaciones con consumidores comprobados en el repositorio y la consolidación de responsabilidades duplicadas. Tres agentes trabajaron en audio, UDP antiguo y transporte; la integración incluyó revisión cruzada y comparación con una copia del árbol al comienzo de esta implementación. Se preservaron los cambios que ya estaban pendientes en el proyecto.

La limpieza reduce el código C# de producción en aproximadamente 636 líneas netas, incluido el nuevo helper de datagramas; este cálculo precede al trabajo VPN concurrente descrito más abajo. El beneficio principal es retirar caminos alternativos sin uso y corregir el control del codificador de voz, no el recuento de líneas. No se ha medido una mejora de rendimiento extremo a extremo.

## Decisiones implementadas

| Hallazgo | Cambio | Beneficio concreto |
| --- | --- | --- |
| H1 | Retirado el códec secundario de VoiceCallHandler. NativeAudioService expone el bitrate de su encoder real y OpusVoiceCodec sincroniza acceso con la codificación. | UI, captura y QoS actúan sobre el mismo encoder. |
| H1 | Cada sesión aporta un límite; se aplica el mínimo de las activas respetando el techo elegido por el usuario. HTTP, WebSocket y selector distinguen bitrate solicitado y efectivo. | Un peer sano no anula las restricciones de otro; guardar ajustes durante congestión no cambia la preferencia. |
| H2 | Eliminados GetPacketReader, SendPayloadAsync, canales de entrega, parser Data e índice por ShortId. El discriminante 0x44 queda reservado. | Desaparece el segundo plano de datos de aplicación y su mantenimiento. |
| H3 | Eliminados CompressedTransparentStream, el parámetro de compresión y CompressionOptions de handlers/mocks. | Los protocolos dejan de declarar una configuración que no se aplicaba. |
| H4 | Retirados métodos vacíos de audio de pantalla, P/Invoke sin llamadas, helpers LAN antiguos, envío directo de chat sin consumidores, PreferredPort de handlers, campos sin lectura y espera descartada. | Menos rutas engañosas y estado innecesario. La configuración real del adaptador permanece. |
| H5 | Retirada la carga y persistencia automática de tickets que el nodo no reutilizaba. | Menos acceso a disco y estado que aparentaba aportar reanudación. |
| H6 | Eliminados TorQuicConnectionFactory, DelegateDummyQuicLaneProvider y sustitución global mutable de la fábrica. | Configuración más local y sin adaptadores sin consumidores. |
| H7 | NativeConnectionTransport y MultiplexedQuicConnectionTransport comparten CategorizedDatagramProtocol. | Una implementación de framing, fragmentación y recepción, con estado independiente por conexión/sesión. |
| H7 | La recepción nativa omite interpretar categorías si no hay consumidores. | El multiplexer consume el datagrama crudo sin trabajo adicional de categorías ni creación de PeerInfo. |
| H8 | Tuning posterior a la conexión centralizado en NativeConnectionTransport; conservada configuración previa al handshake. | Las capas superiores dejan de sobrescribir ajustes explícitos del transporte. |
| H9 | Retirado BroadcastBinaryAudio sin consumidores. | Elimina una salida sin uso conservando compatibilidad de entrada. |
| H10 | Recuperada prueba de reconexión al cambiar endpoint y corregida documentación de rutas, UDP, audio y RelayDrive. | La documentación y las pruebas describen mejor el producto actual. |

## Conservado deliberadamente

- Estado criptográfico del handshake, claves direccionales, nonces y comprobaciones de admisión: IsSessionReady todavía depende de ellos. Retirarlos requiere otro cambio de protocolo.
- APIs nativas de tickets, PeerStore y sus campos persistidos: se mantiene compatibilidad con datos existentes y uso explícito de la librería.
- IQuicConnectionFactory e inyección explícita por conexión: permiten configuraciones locales útiles sin estado global mutable.
- Entrada WebSocket de audio antigua: no hay evidencia suficiente sobre clientes externos para retirarla. Los formatos actuales de pantalla permanecen.
- Tor, Android, Nostr, VPN, multiplexer, decodificadores por peer y adaptación de audio: tienen funciones y consumidores reales.

## Compatibilidad pública

Esta limpieza retira APIs públicas: el canal UDP obsoleto, tipos de compresión/adaptadores y la fábrica global, entre otras. Todos los consumidores de este repositorio se han actualizado. Un proyecto externo que use esos miembros necesitará migrar antes de actualizar la librería; no se ha verificado ningún repositorio externo.

Las aplicaciones del repositorio ya utilizan sesiones QUIC/Tor. No se negocia ni reutiliza el valor wire reservado del antiguo mensaje Data.

## Validación

Compilación limpia del harness y sus referencias de escritorio: correcta, cero errores y 15 advertencias nullable existentes. Compilación Android: correcta, cero errores y 15 advertencias nullable/plataforma. La última compilación incremental integrada también pasa: cero errores y seis advertencias nullable emitidas por el proyecto de pruebas. Se utilizaron directorios de artefactos aislados para no interferir con la aplicación abierta.

Pruebas y logs se encuentran en `.codex-build/cleanup-validation` y `.codex-build/cleanup-final`; la última compilación está en `.codex-build/cleanup-integrated-final`. Las pruebas de certificados y loopback de Windows requirieron salir del sandbox; la primera ejecución restringida no podía importar los certificados temporales.

Resultados confirmados hasta la integración:

- Audio nativo: configuración HTTP hasta los bytes Opus del encoder real, QoS con dos sesiones, desconexión, telemetría tardía, mute/deafen y concurrencia.
- Opus, estado de peers, permisos, seguridad WebUI, pantalla, pooling LAN y antirreplay.
- Multiplexer con chat bidireccional e integridad de archivo concurrente; framing con MTU, fragmentos duplicados/desordenados, aislamiento y limpieza.
- Recuperación y transporte automático.
- Seguridad completa: PASS en 182,3 segundos. Incluye claves direccionales, descarte del UDP antiguo, trust, reconexión al cambiar endpoint y datos QUIC bidireccionales A↔C antes/después de retirar B manteniendo el mismo multiplexer.
- Cancelación de handshake: PASS en 31,1 segundos.
- JavaScript: estado de peers, vídeo de pantalla y selector de bitrate solicitado/efectivo.

Las pruebas originales de datagramas nativos y optimizaciones fallaban en TLS antes de alcanzar el código modificado; se reprodujo el mismo error con la compilación anterior a la limpieza. Se corrigió exclusivamente la importación de sus certificados temporales con X509KeyStorageFlags.Exportable, manteniendo RSA. Cambiar sólo el algoritmo del certificado no resolvía el fallo.

Con el certificado corregido, datagramas nativos pasa con 20/20 mensajes por dirección. La regresión de configuración explícita FIFO también pasa en una pareja de conexiones independiente. La suite general de optimizaciones conserva un fallo previo: espera RoundRobin y lee FIFO. Se reprodujo con su código original y los binarios anteriores a esta limpieza, una vez corregido el certificado. No se ha cambiado producción ni eliminado aserciones para ocultarlo. Las pruebas finales de estos dos archivos se compilaron también en un runner aislado contra el core estable de `cleanup-final`. Evidencia: `quic-datagrams-independent-final.log`, `quic-optimizations-independent-final.log` y `baseline-roundrobin-exportable.log` en `.codex-build/cleanup-validation`.

Durante la validación aparecieron cambios externos concurrentes en `QuicNet/Vpn` y `VirtualLanHandler`. Dos recompilaciones intermedias fallaron por métodos y propiedades aún no incorporados a ese trabajo. Ese trabajo se conservó intacto; no forma parte de esta limpieza. Tras aparecer las piezas pendientes, la compilación integrada volvió a pasar (`integrated-final-build.log`). Los resultados de seguridad completos corresponden a la compilación `cleanup-final`, anterior a esas incorporaciones; no constituyen una revisión funcional de la nueva VPN.

No se han probado dispositivos de audio físicos, un dispositivo Android ni conexiones WAN/Tor reales. La compilación Android y las pruebas locales no sustituyen esas comprobaciones.
