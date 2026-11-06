# Revisión de conectividad y seguridad — 21/09/2026

> Evidencia histórica anterior a la corrección. Véase [Correcciones.md](Correcciones.md) para los cambios y la validación posterior. El ejecutable de esta carpeta ahora exige el comportamiento corregido.

**Cinco defectos: cuatro reproducidos y uno confirmado por revisión estática.** Tres afectan a la integridad de la negociación; los otros dos, al seguimiento de conectividad y a la recuperación tras una migración de red.

Se revisó el estado local, que ya contenía numerosos cambios sin confirmar. No se ha modificado la implementación de la librería. El alcance principal ha sido el establecimiento UDP/QUIC, la señalización, la identidad del interlocutor y el mantenimiento de conexiones. No es una auditoría exhaustiva de Tor, las dependencias nativas ni todas las topologías NAT.

## 1. P1 — Una respuesta puede resolver la negociación de otro interlocutor

**Reproducido.** [HandshakeHandler.cs:368](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/PacketHandler/HandshakeHandler.cs:368), [HandshakeManager.cs](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/Helpers/HandshakeManager.cs).

El receptor valida la firma con el certificado del emisor del paquete, pero después llama a Approve/Reject utilizando únicamente el GUID de negociación. No comprueba que ese emisor sea el peer al que se dirigió la solicitud ni que coincidan el protocolo y el transporte. El gestor tampoco conserva esa asociación para validarla.

En la reproducción, A tenía una decisión pendiente dirigida a B, incluyendo explícitamente su identidad. C era conocido por descubrimiento pero no confiable. Un Accept firmado por C resolvió la decisión de B como aceptada con el puerto elegido por C.

**Condición de ataque:** conocer el GUID de la negociación, que circula en la señalización UDP sin cifrar, y tener una identidad descubierta. La misma omisión afecta a las respuestas de rechazo. Permite cancelar o desviar el establecimiento; la prueba no demuestra suplantación del certificado en el QUIC posterior.

**Corrección:** asociar cada negociación a identidad, protocolo y transporte, y validar esa asociación antes de aprobar/rechazar o mutar el peer.

## 2. P1 — Un handshake capturado puede cambiar el destino de control por replay

**Reproducido.** [HandshakeHandler.cs:82](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/PacketHandler/HandshakeHandler.cs:82).

Tras verificar la firma se escribe ActiveEndPoint con la dirección de origen del datagrama, antes de comprobar si corresponde a una negociación vigente. El formato firmado no incluye caducidad ni vínculo con la sesión actual. El control de duplicados de las solicitudes llega después de esa escritura y no protege las respuestas.

La reproducción procesó dos veces exactamente el mismo Decline firmado por B, primero desde su destino habitual y después desde otro puerto. La segunda copia cambió ActiveEndPoint aunque no existía ninguna negociación pendiente.

**Impacto:** quien capture un paquete válido puede reenviarlo desde otra dirección y desviar allí la señalización y el canal UDP heredado mientras siga disponible ese peer. No necesita su clave privada. No cambia por sí solo el destino de un túnel QUIC ya establecido.

**Corrección:** comprobar contexto y frescura antes de cualquier cambio de estado. Para aceptar una dirección nueva, exigir una comprobación de retorno autenticada asociada a esa dirección.

## 3. P1 — La apertura UDP acepta ACK/FinalHandshake sin autenticación

**Reproducido.** [QuicPunchConnection.cs:284](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/QuicPunchConnection.cs:284).

ReceiveHoleLoopAsync valida el paquete firmado nuevo, pero también acepta el formato antiguo con solo el GUID de conexión y el identificador del peer. Esa rama no verifica firma ni prueba de contraseña y devuelve inmediatamente el origen como destino seleccionado. SendLoopAsync sigue enviando el formato antiguo junto al firmado.

En la reproducción se configuró una contraseña, y un socket independiente envió un ACK sin firma, sin conocer la contraseña. Su dirección fue seleccionada por la implementación.

**Condición de ataque:** obtener los identificadores públicos de ese intento y llegar antes que la respuesta legítima. Puede hacer fallar el establecimiento o desviarlo; el pinning TLS sigue siendo una barrera posterior.

**Corrección:** eliminar la aceptación del formato antiguo en el modo seguro o autenticar también ACK/FinalHandshake, vinculándolos al intento y al destino comprobado.

## 4. P2 — Respuestas de ping falsas mantienen al peer como conectado

**Reproducido.** [PingHandler.cs:35](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/PacketHandler/PingHandler.cs:35), [PeerInfo.cs](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/Structures/PeerInfo.cs).

La respuesta Ping solo necesita un identificador conocido y un timestamp reciente y creciente. No exige firma, no verifica el origen ni comprueba que se hubiese enviado una solicitud con ese timestamp. Aun así actualiza LastPingResponseUtc, que IsConnectedAndResponsive utiliza para decidir si el peer sigue vivo.

La reproducción envió una respuesta sin solicitud previa desde un puerto diferente y actualizó la actividad del peer. El comentario de que los pings no autenticados no deben mantener peers vivos solo se cumple para la rama de solicitudes.

**Impacto:** estado de conexión y latencia falsos y retraso de la eliminación de peers desconectados. Esto no evita el timeout autenticado propio del multiplexor QUIC.

**Corrección:** usar ping/pong autenticado con un desafío pendiente, o impedir que una respuesta UDP sin autenticar determine la vigencia del peer.

## 5. P2 — La migración QUIC sobrescribe el destino del canal de control

**Revisión estática; no se ha simulado una migración real.** [QuicPunchConnection.cs:150](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/QuicPunchConnection.cs:150), [QuicPunch.cs:2591](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/QuicPunch.cs:2591), [QuicPunch.cs:1002](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/QuicPunch/QuicPunch.cs:1002).

El túnel QUIC se crea con un socket/puerto separado del socket de descubrimiento y control. Sin embargo, OnPeerAddressChanged copia el endpoint del túnel a remotePeer.ActiveEndPoint y lo guarda. SendToPeerAsync y la negociación utilizan ese campo para enviar el protocolo de control.

Cuando llega un evento de migración con un puerto distinto al de control, las posteriores solicitudes, pings o reconexiones se envían al puerto de MsQuic, que no procesa esos datagramas de control.

**Corrección:** mantener separados el endpoint del túnel y el de control; actualizar el segundo mediante descubrimiento o comprobación autenticada propia.

## Validación

| Comprobación | Resultado |
| --- | --- |
| Compilación de QuicPunchTests y referencias | Correcta, 0 warnings y 0 errores |
| Reproducciones específicas de los hallazgos 1–4 | 4/4 confirmadas |
| Control positivo con TryRunServer/TryRunClient de producción | QUIC local establecido con mTLS y pinning |
| --test-antireplay | Pasa |
| --test-punch-packet | Pasa; no cubre la rama sin firma del receptor |
| --test-pqc | Pasa; se observa X25519MLKEM768 en el cliente |
| --test-mux | Falla durante el establecimiento TLS con UserCanceled |
| --test-security | Se interrumpe por timeout en SecurityDiscoveryTests.cs:2393 |
| --test-handshake-cancellation | Falla esperando Accept en HandshakeCancellationLifecycleTests.cs:291 |

Los fallos de las suites se registran como limitaciones de validación, no como vulnerabilidades adicionales demostradas. En las dos últimas hay espera de aceptación tras la obtención de candidatos; el log de lifecycle muestra candidatos disponibles después de vencer la espera del test. La prueba positiva de mTLS y la prueba PQC pasan, por lo que no se puede concluir que el fallo de --test-mux invalide todo QUIC.

## Repetir las reproducciones

[Program.cs](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/doc/audits/connectivity-2026-09-21/Program.cs) contiene las cuatro reproducciones y el control positivo. [Audit.csproj](C:/Users/Mrgaton/Syncthing/Programs/CSharp Test Programs/UdpPunchHoleTest/doc/audits/connectivity-2026-09-21/Audit.csproj) usa los binarios de Debug previamente compilados y el nombre de ensamblado autorizado para las pruebas internas. Preparado y verificado en Windows x64 con el SDK .NET 11 disponible.

Desde la raíz del repositorio:

    dotnet build QuicPunchTests/QuicPunchTests.csproj --no-restore
    dotnet run --project doc/audits/connectivity-2026-09-21/Audit.csproj

La reproducción utiliza sockets de loopback y nodos con el servicio WAN desactivado. Los constructores existentes pueden iniciar servicios auxiliares como sincronización horaria o actualización de reputación; no debe interpretarse como un proceso completamente aislado de Internet. Las identidades de prueba se guardan en directorios temporales qp-audit-*.

El programa termina correctamente cuando los cuatro defectos siguen presentes. Es una reproducción de auditoría, no una suite que exija el comportamiento corregido. Una corrección deberá invertir esas expectativas y añadir cobertura de integración para cada caso.
