# Verificación Android–Windows, 2026-09-30

## Entorno y resultado

OnePlus CPH2769 ARM64, Android API 36, y escritorio Windows en la misma LAN. APK Debug recompilada e instalada correctamente. Se conserva la interfaz `QuicNet.Shared/wwwroot` y el motor compartido; Android adapta transporte nativo, audio y ciclo de vida.

| Prueba | Resultado |
| --- | --- |
| Autoprueba nativa Android | TLS y stream loopback correctos; grupo TLS 4588 en ambos extremos. |
| Sesión física Android–Windows | Establecida; endpoints nativos de la LAN confirmados por telemetría. |
| Criptografía | `X25519MLKEM768` y `TLS_AES_256_GCM_SHA384` observados. Certificados de identidad ECDSA P-256, sin firmas poscuánticas. |
| Chat directo | Mensajes recibidos en ambos sentidos por QUIC; `viaNostr=false`. |
| Velocidad | Prueba de humo de 2 s: 169,61 Mbps de subida y 1112,8 Mbps de bajada; sin error. No constituye un benchmark sostenido. |
| Voz | Sesión activa en ambos dispositivos; captura Android activa y sin silenciar, datagramas intercambiados. El usuario confirmó que la llamada se escucha. |
| Segundo plano | Sesiones y tráfico mantenidos con servicio en primer plano; muestras de pantalla apagada y estado Dozing sin bloqueo efectivo de red. El usuario despertó el teléfono durante algunas pruebas, por lo que no demuestran Doze prolongado. |
| Latencia de red | Muestra final en reposo: RTT 5,67 ms; sin fallos de descifrado. No equivale a latencia de audio de extremo a extremo. |
| Búfer de salida Android | Registro final: 40,0 ms, frente a 120,1 ms anteriores. El dispositivo informa modo `None` pese a solicitar `LowLatency`. |

## Correcciones aplicadas

- El nodo y el servidor HTTP viven en `AndroidConnectionService`, separado de la WebView. El servicio declara conexión y micrófono, se inicia desde la actividad visible y ofrece Desconectar en la notificación. No se reinicia automáticamente tras una parada; cerrar la tarea lo detiene.
- Esperas de conexión, lectura/aceptación y vaciado de streams Android consultan el puente nativo sin bloquear monitores durante 250 ms; esperan de forma cancelable entre consultas. Esto evita bloquear la API y la interfaz.
- El puente nativo guarda el grupo y cifrado TLS en el evento de conexión, antes de que se descarte el contexto TLS. La API publica el grupo realmente negociado.
- `WebUiServer` publica su instancia después de inicializar el hub, corrigiendo una excepción de arranque en escritorio.
- Cola de recepción de audio compartida: máximo tres tramas de 20 ms. Tras una pausa de reproducción se descarta voz antigua para recuperar el tiempo real.
- Windows: colas de captura y reproducción reducidas a tres tramas; no se sobrescriben buffers WinMM en uso.
- Android: capacidad de AudioTrack suficiente para el controlador, con tamaño activo solicitado de 1920 muestras a 48 kHz (40 ms). Captura mantiene el mínimo exigido por el dispositivo.

## Comprobaciones de código

Compilaciones Android y escritorio sin errores. Pasaron las pruebas de handshake poscuántico, Opus y audio nativo, incluida recuperación tras bloquear deliberadamente la reproducción. El puente nativo Linux pasó streams, datagramas, grupo poscuántico y rechazo de certificados incorrectos. La última APK se reinstaló y repitió chat, velocidad y voz con éxito.

Los archivos temporales de prueba y los registros están en `.artifacts/android-verification/` (ignorados por Git). No son necesarios para compilar.

## Trabajo pendiente

1. Medir retardo acústico real en ambos sentidos y contar underruns de AudioTrack durante llamadas largas; ajustar el búfer si aparecen cortes. La reducción de 80 ms corresponde al tamaño del búfer, no a una mejora acústica ya medida.
2. Validar cambios Wi-Fi/datos, pérdida de cobertura, reconexión y Doze prolongado en más fabricantes. Al reinstalar o forzar parada, el otro extremo puede conservar temporalmente la sesión anterior; liberarla antes de repetir una prueba manual.
3. Verificar vídeo y compartir pantalla de extremo a extremo, capacidades y permisos Android. La voz no valida estos componentes.
4. Validar empaquetado Release y distribución en otro dispositivo ARM64. Esta sesión comprobó el APK Debug.
5. Mantener los HTML, protocolos y almacenamiento compartidos; limitar las nuevas adaptaciones Android a APIs de plataforma. VPN Android continúa fuera del alcance acordado.
