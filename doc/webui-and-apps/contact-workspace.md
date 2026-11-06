# Agenda, historial y presencia de descubrimiento

La pantalla Inicio muestra una lista compacta, agrupada en conectados, conectando, disponibles y sin conexión. Dentro de cada grupo se ordena por último uso; cuando falta ese dato se usa la última conexión y después el nombre. Agenda (todos, guardados o identidades sin verificar) y estado de conexión son filtros independientes dentro de una barra compacta. Las alternativas de ordenación se conservan localmente. Las invitaciones se comparten o introducen en un diálogo; los servicios WAN/Tor y los códigos avanzados están en Ajustes → Conexión y Tor.

## Modos de conexión

Inicio expone WAN, Solo Tor y Ambas. El controlador de modos comparte una exclusión mutua con los controles individuales de Ajustes. Persiste la intención antes de parar servicios, detiene primero el transporte excluido y después activa el solicitado. Un fallo de Tor no restablece WAN automáticamente. Detener un servicio cancela sus negociaciones y cierra también los multiplexores y sesiones existentes; los contactos guardados no pueden reabrir ese transporte mientras siga apagado.

La paleta lila se aplica al estado real Solo Tor, después de que Tor esté activo y WAN detenido. Durante las transiciones y los errores se muestra texto explícito. El descubrimiento se configura por separado: una preferencia de descubrimiento WAN habilitada no inicia sus relays si WAN está apagada. Al cambiar de modo se reinicia el buzón para cerrar conexiones de relay anteriores y usar el proxy del transporte actual; con WAN desactivada, el buzón no abre conexiones antes de que Tor esté disponible. Esto controla las comunicaciones de la aplicación, no la conectividad de otras aplicaciones del sistema.

«Conectar por código» está siempre visible. Si el código requiere un servicio apagado, el formulario conserva el código y ofrece una acción explícita para cambiar al modo correspondiente y reintentar. «Mi código» abre la opción de compartir. La conexión manual funciona independientemente del descubrimiento.

## Aviso de privacidad del descubrimiento Tor

El descubrimiento Nostr por Tor está desactivado por defecto, independientemente del servicio Tor. Activar el servicio o cambiar de modo de conexión no activa el descubrimiento. La interfaz presenta un aviso breve y exige marcar «He leído el aviso y acepto el riesgo de perder el anonimato» antes de habilitar el botón de activación. Cancelar o pulsar Escape no envía ninguna solicitud de activación. Desactivar es inmediato y no exige confirmación.

El aviso explica que publicar la dirección onion y el nombre del dispositivo permite cotejar nombres y horarios con los anuncios WAN y potencialmente vincular la identidad Tor con la IP pública. Las claves separadas no eliminan la correlación de los datos publicados. Los relays pueden conservar copias después de desactivar la opción. No se afirma una probabilidad cuantificada de desanonimización. El texto se apoya en las recomendaciones de [seguridad operacional de servicios onion](https://community.torproject.org/onion-services/advanced/opsec/) y la explicación del Proyecto Tor sobre [correlación y análisis temporal](https://support.torproject.org/about-tor/security/attacks-on-onion-routing/).

`AppPreferencesStore` conserva `TorDiscoveryRiskAcknowledged`, inicialmente falso, junto a `TorNostrDiscoveryEnabled`. Una configuración antigua que tenía el descubrimiento habilitado sin esta aceptación se carga con el descubrimiento apagado; el servicio Tor y las identidades existentes no cambian. La aceptación explícita se conserva al reiniciar y se borra al desactivar, de modo que una nueva activación requiere leer el aviso otra vez.

`POST /api/discovery` exige `acknowledgeTorPrivacyRisk: true` para habilitar el ámbito `tor`, también si lo solicita un cliente que no usa la interfaz actual. Sin ese booleano devuelve 409 sin activar el descubrimiento ni cambiar las preferencias. La preferencia y la aceptación se guardan antes de arrancar el descubrimiento. Los cambios Tor se serializan y un fallo de arranque desactiva la opción. El ámbito WAN conserva su flujo independiente.

`NotificationTests` comprueba valores iniciales, migración, persistencia y el contrato HTTP de aceptación. `TorDiscoveryPrivacyBrowserTests.mjs` comprueba el aviso previo a la solicitud, aceptación sin premarcar, cancelación/Escape, nueva aceptación al reactivar, rechazo del servidor, desactivación, ámbito WAN y diálogos en ventanas de 320 a 1280 píxeles. Las pruebas usan datos ficticios sin iniciar conexiones Tor reales.

## Historial y localidad

`PeerStore` guarda `LastConnectedUtc`, `LastKnownIp`, `LastUsedUtc` y `UsageCount` como propiedades adicionales compatibles con las agendas existentes. Los registros antiguos no inventan una fecha de conexión. Una conexión autenticada registra su fecha y dirección; guardar un contacto que ya está conectado también registra esa información. Los cambios de IP observados en un transporte conectado se recogen en el bucle de mantenimiento.

El uso cuenta periodos de actividad de un minuto al enviar mensajes o completar conexiones de aplicación, evitando escribir en disco por cada mensaje. Los anuncios, pings y refrescos de la interfaz no incrementan ese contador.

La localidad se consulta en la base GeoIP local existente. Los contactos desconectados usan la última IP observada, con la etiqueta «Última IP» y la fecha de su última conexión; si aún no existe esa observación se pueden consultar las direcciones guardadas/anunciadas. La interfaz muestra una aproximación de la IP, no la ubicación física de la persona. Las conexiones Tor no guardan una IP de relay como ubicación y mantienen su localidad oculta.

## Descubrimiento recíproco

Los anuncios WAN y Tor conservan el token y su prueba de certificado y añaden una propiedad `presence`: un identificador aleatorio de esa ejecución del descubrimiento. Ambos ámbitos usan identificadores diferentes. El evento Nostr firmado cubre el contenido del anuncio.

Al recibir una presencia nueva de una identidad de confianza cuyo token y autor pasan las comprobaciones existentes, se vuelve a publicar el token local. Una tabla limitada a 256 identidades evita responder repetidamente a la misma ejecución; las publicaciones recíprocas se agrupan y limitan a una por segundo y ámbito. El reinicio del descubrimiento cambia la presencia aunque la IP y el puerto sean iguales. Los lectores antiguos pueden ignorar la propiedad adicional y seguir usando el token.

Los contactos guardados con conexión automática mantienen el transporte cuando están inactivos. Los demás conservan la política de cierre por inactividad. Buscar una invitación desde la interfaz establece el transporte sin conceder confianza para los protocolos: la revisión de identidad sigue siendo una decisión explícita.

Las conexiones WebSocket usan PING/PONG con intervalo y timeout de diez segundos, y los envíos tienen un plazo de diez segundos. La interfaz distingue entre un servidor conectado y la confirmación `OK` de un anuncio enviado. Esta confirmación indica recepción por el servidor, no conexión con una persona. La reconexión existente sigue recuperando los relays caídos. `LanDiscoveryPort = 0` desactiva la escucha y los anuncios LAN.

## Huella visual del certificado

La agenda sustituye el identificador hexadecimal abreviado por siete símbolos geométricos. La marca depende de la identidad del certificado, no del nombre, la foto, la dirección de red ni el identificador de una conexión. Se conserva la foto de perfil para reconocer a la persona; la secuencia permite distinguir certificados que compartan nombre o foto. Además del color, cada símbolo tiene una forma y una variante rellena o hueca.

Los precedentes son los [identicons de GitHub](https://docs.github.com/en/account-and-profile/reference/profile-reference), los [dibujos de claves de OpenSSH](https://man.openbsd.org/ssh.1#VERIFYING_HOST_KEYS) y la comparación de [huellas completas en Signal](https://support.signal.org/hc/en-us/articles/360007060632-What-is-a-safety-number-and-why-do-I-see-that-it-changed). La marca es una ayuda de reconocimiento; un dibujo parecido no demuestra que una identidad sea la esperada. Al pulsarla, un diálogo accesible muestra la misma secuencia ampliada y la huella completa, seleccionable y copiable. La revisión de contactos usa esa misma representación y mantiene la decisión de confianza explícita.

`identity.js` normaliza los 32 bytes completos de `certHash`, admitiendo hexadecimal, Base64 y Base64url. Ese valor es la identidad SHA3-256 de la clave pública que ya utiliza el protocolo. La versión visual v1 calcula SHA-256 sobre el prefijo UTF-8 `QuicNet visual identity v1` seguido de un byte cero y esos 32 bytes. Para cada símbolo `i`, el byte `2*i` selecciona una de 16 formas con sus cuatro bits bajos y la variante con el bit 4; el byte `2*i+1` selecciona uno de ocho colores con sus tres bits bajos. Son 56 bits de decisiones gráficas, una reducción de la huella completa, sin garantía de unicidad ni de comparación humana exacta. El orden de formas, colores y variantes es estable y tiene un vector de regresión: no debe cambiarse silenciosamente en una actualización.

Los SVG se generan localmente con geometría fija, sin servicios externos, imágenes elegibles por el contacto ni nuevas dependencias. Cuando falta el certificado completo se muestra «Identidad pendiente»; no se fabrica una identidad a partir del nombre o del hash truncado. Si Web Crypto no está disponible, se mantiene el acceso a la huella completa sin mostrar una marca inventada.

Cuando una identidad sin confianza coincide en nombre con un contacto guardado de confianza y sus certificados completos difieren, se muestra «Mismo nombre · otra identidad». El cotejo del nombre normaliza Unicode NFKC, espacios exteriores y mayúsculas/minúsculas. El aviso no declara una suplantación ni concede o revoca confianza; la identidad debe comprobarse con la otra persona.

`CertificateIdentityWebTests.mjs` verifica el vector v1, equivalencia de codificaciones, uso de todos los bytes y estabilidad al cambiar el nombre. `CertificateIdentityBrowserTests.mjs` verifica marcas distintas para nombres copiados, huella completa/copia, aviso de coincidencia, foco y teclado, ausencia de confianza implícita y peticiones externas, nombres tratados como texto y diseño de 320 a 1280 píxeles.

## Verificación

Compilación aislada para no reemplazar los binarios de una aplicación abierta:

```powershell
dotnet build QuicPunchTests/QuicPunchTests.csproj --no-restore -c CodexValidation -m:1 /nodeReuse:false /p:UseSharedCompilation=false
```

Pruebas de historial, descubrimiento firmado con un relay WebSocket local, transporte nativo y geolocalización:

```powershell
dotnet QuicPunchTests/bin/CodexValidation/net11.0/QuicPunchTests.dll --test-contact-history --loopback-only
dotnet QuicPunchTests/bin/CodexValidation/net11.0/QuicPunchTests.dll --test-discovery-presence --loopback-only
dotnet QuicPunchTests/bin/CodexValidation/net11.0/QuicPunchTests.dll --test-automatic-transport --loopback-only
dotnet QuicPunchTests/bin/CodexValidation/net11.0/QuicPunchTests.dll --test-geolocation --loopback-only
```

El aislamiento de estas suites sustituye relays y catálogos STUN por destinos loopback, usa el reloj del sistema y bloquea HTTP externo mediante un proxy loopback. Las pruebas de transporte desactivan broadcast LAN. No se modifican los valores de producción ni se abre la agenda del usuario.

Las pruebas `ContactHomeBrowserTests.mjs`, `ContactReviewBrowserTests.mjs` y `ContactSignalsBrowserTests.mjs` usan la vista previa con datos ficticios: ordenación persistente, filtros independientes, foco, errores, copia manual cuando falla el portapapeles, ubicación histórica, privacidad Tor y ventanas de 320 a 2560 píxeles. No miden el tiempo de conexión entre redes reales ni permiten garantizar conexiones instantáneas bajo cualquier NAT.


Las suites adicionales `--test-connection-modes` (sin red) y `ConnectionModesBrowserTests.mjs` verifican exclusividad, serialización de cambios, fallo de Tor sin restauración WAN, paleta por estado real, activación explícita desde un token, agrupación y orden por último uso. `--test-automatic-transport --loopback-only` verifica también el cierre de una conexión nativa al detener WAN y la ausencia de reapertura automática. Las pruebas usan servicios simulados para el arranque de Tor; no realizan una conexión a la red Tor pública.
