# Correcciones de conectividad — 21/09/2026

Se han corregido las cinco rutas descritas en `Informe.md`. El informe conserva la evidencia del estado anterior; `Program.cs` ahora ejecuta las regresiones y termina correctamente únicamente cuando las comprobaciones del comportamiento corregido pasan.

## Cambios

1. `HandshakeManager` conserva una copia de la identidad y del certificado esperado, protocolo, transporte, dirección de control y sentido de la negociación. El receptor solo completa respuestas coincidentes y pendientes. Las decisiones locales no pueden completarse desde la red. La finalización y los temporizadores retiran la entrada concreta de forma atómica.
2. Los handshakes llevan un timestamp firmado: máximo 25 segundos de antigüedad y 5 segundos de adelanto. No modifican la dirección ni el transporte de control. En WAN deben proceder de la dirección de control ya conocida; una dirección nueva debe establecerse mediante el descubrimiento y sus comprobaciones. Las sesiones entrantes también comprueban identidad, protocolo y transporte antes de reutilizar respuestas.
3. La apertura UDP usa el sobre `QCP2` de 308 bytes: GUID de intento, desafío, eco, prueba de identidad/contraseña de 192 bytes y firma del conjunto. La selección exige devolver el desafío aleatorio que se envió a esa misma dirección durante ese intento. La comprobación de contraseña sigue usando la prueba HMAC existente. Se admiten retransmisiones autenticadas, pero ni ACK/FinalHandshake antiguos ni la prueba de 192 bytes aislada seleccionan una dirección. Sin clave de firma, la apertura falla.
4. Los pong heredados, sin firma ni desafío pendiente, no actualizan actividad ni latencia. El multiplexor mantiene las mediciones sobre su conexión autenticada. Los peers sin multiplexor dependen de otro tráfico autenticado para mantenerse activos.
5. `PeerInfo.QuicEndPoint` conserva la ruta del túnel. El establecimiento y el callback de migración actualizan ese campo; no escriben `ActiveEndPoint` ni guardan el puerto del túnel como puerto de control.

**Compatibilidad:** deben actualizarse ambos extremos. El formato de handshake incorpora ocho bytes de timestamp después del GUID de negociación y antes de los candidatos. La apertura UDP ya no ofrece degradación al formato sin autenticar. El ping UDP heredado deja de proporcionar mediciones de latencia.

## Repetición

La suite específica está en `QuicPunchTests/Tests/ConnectivitySecurityTests.cs`. El ejecutable de esta carpeta compila ese mismo archivo y referencia directamente el proyecto de la librería, evitando utilizar DLL antiguas:

```powershell
dotnet run --project doc/audits/connectivity-2026-09-21/Audit.csproj
```

Desde el ejecutable principal de pruebas:

```powershell
dotnet build QuicPunchTests/QuicPunchTests.csproj --no-restore -p:OutputPath=bin/ConnectivityValidation/
dotnet QuicPunchTests/bin/ConnectivityValidation/QuicPunchTests.dll --test-connectivity-security
```

La salida independiente evita los bloqueos de las DLL usadas por QuicNet/Visual Studio. Para compilar el conjunto hubo que corregir también una llave de cierre sobrante y la propiedad `CompressionOptions` ausente en el mock de `NostrMailboxTests`.

La regresión cubre Accept/Decline/Unsupported de otro firmante, protocolo, transporte y dirección; certificado esperado incorrecto; aprobación local; expiración y cancelación; replay de solicitudes/respuestas; solicitudes correctamente firmadas pero caducadas; pongs falsos; separación de rutas; ACK y FinalHandshake sin firma; prueba antigua aislada; intento incorrecto; contraseña ausente o incorrecta; firma alterada; respuesta de desafío reproducida desde otro puerto; y respuesta capturada de un intento anterior.

Los controles positivos completan apertura UDP de producción con y sin contraseña, mTLS con pinning y transferencia de un byte por el stream QUIC. La prueba de migración ejecuta la misma actualización usada por el callback; **no simula una migración real de MsQuic/NAT**. Los nodos de la suite específica desactivan WAN, UPnP y relés Nostr, pero los constructores pueden iniciar servicios auxiliares; no es una prueba completamente aislada de Internet.

## Validación

- Compilación del conjunto: correcta, 0 errores; 18 advertencias de nulabilidad en pruebas existentes.
- `--test-connectivity-security`: pasan las 40 comprobaciones.
- `--test-antireplay`: pasa.
- `--test-punch-packet`: pasa.
- `--test-handshake-cancellation`: pasa, tras usar la apertura autenticada y permitir hasta 15 segundos para obtener candidatos.
- `--test-security`: pasa el conjunto completo, incluida la apertura con puertos asimétricos, idempotencia y reutilización de conexiones. El log también recoge excepciones de tareas auxiliares no observadas (`ObjectDisposedException`); no se ha investigado aquí ese problema adicional.
- `--test-mux`: persiste el error TLS `UserCanceled` descrito en el informe original.

No se ha probado interoperabilidad con versiones antiguas, migración real entre redes ni todas las topologías NAT.
