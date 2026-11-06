# Inestabilidad de conexión: revisión del 18 de octubre de 2026

## Evidencia del fragmento recibido

- A las 12:51:12.926 el canal de control termina con `QUIC_STATUS_INTERNAL_ERROR`; el lector de streams y el test de velocidad fallan a continuación. El fragmento no identifica el motivo nativo que provocó ese primer cierre.
- Una tarea del emisor del test de velocidad termina con una excepción no observada. El camino de error del lector cancela el run y puede saltarse el `await` normal del emisor.
- En la reconexión, los candidatos anuncian una IP pública distinta de la dirección de control observada. La búsqueda dinámica solo recorría las IP anunciadas, omitiendo esa dirección actual.
- Tras nominar un endpoint, el primer intento del cliente QUIC falla porque el puerto sigue ocupado por su propio socket de sondeo. El siguiente intento funciona después de cerrar ese socket.
- Los avisos de UPnP y las retransmisiones idempotentes de solicitudes no prueban por sí solos la causa del cierre original.

## Correcciones

1. La búsqueda acotada incluye la dirección de control observada y los puertos reflexivos del túnel, manteniendo los candidatos LAN asociados a su puerto exacto. No se cambia la validación de identidad ni la nominación autenticada del endpoint.
2. Cliente y servidor liberan el socket de sondeo justo antes de que MsQuic tome el mismo puerto nominado. Se evitan propietarios UDP simultáneos y el reintento contra el socket de la propia aplicación.
3. El test de velocidad cancela y espera a su tarea de subida en `finally`, incluso cuando el lector o el monitor fallan antes de llegar a su espera normal. El buffer de envío se devuelve antes de dar por concluida la limpieza.
4. Los cierres registran la identidad/endpoint del peer, `QuicError`, códigos de transporte/aplicación y el estado nativo de cierre. El vencimiento de un frame de control incompleto también queda diferenciado de una cancelación normal. Los campos nativos siguen los [eventos de conexión de MsQuic](https://github.com/microsoft/msquic/blob/main/docs/api/QUIC_CONNECTION_EVENT.md).

## Validación

- La prueba de IP pública observada falla con el código anterior y pasa con el cambio.
- La interrupción de una subida durante la fase dúplex reproduce dos excepciones no observadas con el código anterior; pasa después de asegurar la limpieza de tareas.
- `--test-birthday-punch`: selección del endpoint y socket ganador, conservación del puerto, ausencia de reintentos por el socket propio y cancelación de caminos perdedores.
- `--test-speedtest`: pruebas reales sobre QUIC local con y sin datagramas, fases, cancelación, fallos de subida y ausencia de tareas huérfanas.
- `--test-session-recovery` y `--test-connection-recovery`: cortes de transporte, restauración de sesiones e inicio simultáneo.

Las pruebas usan conexiones locales; no reproducen la red móvil/NAT de los dos equipos del usuario. La causa exacta del primer error interno de MsQuic permanece sin atribuir. Los nuevos códigos de cierre permiten distinguirla si vuelve a ocurrir.
