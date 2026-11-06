# Chat: conexión automática y pausa temporal de Nostr

Abrir una conversación privada intenta conectar automáticamente usando `/api/connect-peer` y el protocolo de chat existente. Si el contacto solo está guardado, se solicita primero su conexión mediante `/api/connect-saved-peer`. No se cambia el protocolo, el historial ni los permisos de confianza.

Mientras conecta se puede escribir; el envío, los adjuntos y las notas de voz se habilitan cuando existe una sesión de chat. El intento dura hasta 25 segundos, sin solicitudes duplicadas para el mismo contacto. Tras un fallo se muestra Reintentar y se espera 30 segundos antes del siguiente intento automático mientras esa conversación permanezca abierta. Una sesión existente se reutiliza. No se abren conversaciones automáticamente desde otras secciones de la aplicación.

El borrador permanece en memoria durante la sesión. No se envía automáticamente después de un fallo ni se entrega a relés. La petición de envío conserva el destinatario que tenía al pulsar Enviar; cambiar de conversación mientras termina la petición no borra el borrador de otro contacto. El historial local anterior permanece disponible.

`ChatHandler.NostrMessagingEnabled` está desactivado por defecto. La implementación de Nostr se conserva para trabajo futuro, pero el chat no adjunta el buzón, no recibe mensajes a través de él ni utiliza el envío offline como alternativa a una conexión fallida. El canal público se oculta y su endpoint rechaza nuevos envíos mientras esté pausado. Un enlace antiguo al canal permite consultar su historial, con el envío desactivado.

El contrato de protocolos incorpora `SupportsMailbox` (activado por defecto para los demás protocolos). El registro, la recepción y el trabajador de reintentos respetan esa capacidad; los pendientes antiguos del protocolo de chat no se retransmiten automáticamente mientras esté desactivada. El descubrimiento Nostr y el buzón de otros protocolos siguen disponibles. No se borran datos anteriores.

## Comprobaciones

- Compilación C# sin errores (seis avisos de nulabilidad existentes en SecurityDiscoveryTests).
- `--test-webui-security`: pausa de Nostr, rechazo HTTP de envíos sin sesión o al canal público, envío directo correcto y fallo directo sin alternativa offline, además de las pruebas existentes.
- `--test-mailbox`: regresión del buzón y despacho de otros protocolos.
- `ChatConnectionWebTests.mjs`: apertura automática, deduplicación, reutilización de sesión, contactos guardados, tiempo límite, espera entre reintentos y protección de borradores/destinatarios.
- `ChatNavigationWebTests.mjs` y `UiStructureWebTests.mjs`.
- Navegador con datos de prueba: abrir contacto → conexión automática → enviar; contacto no disponible → borrador editable y envío deshabilitado. Esta comprobación no sustituye una llamada o conexión real entre dos equipos.

La copia anterior a estos cambios está en `backups/chat-direct-2026-10-12/before-chat-direct.zip`, con nueve archivos y un manifiesto SHA-256. La copia del rediseño original también se conserva. Extraer a una carpeta aparte para comparar antes de restaurar archivos concretos.
