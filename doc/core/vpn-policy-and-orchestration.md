# VPN: políticas de IP, autoconexión y servicio de adaptador

QuicNet ofrece dos modos sobre el mismo transporte autenticado por certificados QUIC. La configuración se guarda en `vpn.json` dentro de `NodeAppDataPath` (por defecto, `%APPDATA%/QuicPunchV17` en Windows). La pantalla VPN muestra la ruta exacta del archivo y permite editar las reglas de cada peer.

El modo se elige por contacto: LAN automática y LAN avanzada pueden coexistir en el mismo adaptador. La dirección local, máscara y MTU siguen siendo comunes. Cada entrada guarda `mode` (`simple` o `advanced`) y sus propias opciones de inicio automático. Los archivos antiguos que no incluyen `peers[].mode` conservan el comportamiento de su `mode` global hasta editar ese contacto.

## Modos y autorización

**LAN automática** asigna direcciones estables dentro de `10.0.0.0/24`. La dirección depende de la identidad y del grupo del nodo. El peer solo puede enviar paquetes con su dirección asignada; puede acceder a la IP virtual local y usar broadcast/multicast para descubrimiento. Este modo no comparte automáticamente la LAN física ni convierte el ordenador en una salida a Internet. Es una red de capa 3 IPv4, sin Ethernet, ARP ni DHCP.

**VPN avanzada** empieza con esas mismas direcciones y permite sustituirlas, definir redes remotas, limitar destinos y autorizar reenvío. Las reglas se vinculan a la huella de identidad del peer, no a su nombre, endpoint UDP o dirección que anuncia. No se añaden claves VPN: se reutiliza la identidad autenticada del protocolo.

| Campo por peer | Significado desde este ordenador |
| --- | --- |
| `mode` | `simple` para LAN automática o `advanced` para LAN avanzada. Si falta, se hereda el modo global del archivo antiguo. |
| `virtualAddress` | IP virtual exacta que ese peer debe anunciar; vacío usa la asignación automática. Siempre se autoriza como origen y ruta `/32`. |
| `allowedSources` | Otros orígenes IPv4/CIDR que ese peer puede enviar, por ejemplo una LAN situada detrás de él. |
| `accessibleDestinations` | Destinos a los que ese peer puede iniciar tráfico al llegar a este ordenador. |
| `routes` | Destinos de mi tráfico que deben enviarse hacia ese peer. |
| `autoConnect` | Abrir la sesión VPN y reintentar cuando el peer esté disponible. |
| `autoAccept` | Aceptar su sesión VPN sin una petición local. No concede permisos a otros protocolos. |
| `paused` | Bloquear nuevas sesiones y tráfico en ambos sentidos conservando la propiedad de las rutas. |
| `enabled` | Incluir al peer en la política VPN. |
| `allowBroadcast` | Permitir broadcast/multicast en modo avanzado. |
| `allowForwarding` | Permitir que su tráfico autorizado se reenvíe a otros peers o redes físicas. |

Las rutas y los orígenes son decisiones separadas. Para recibir respuestas desde una LAN remota, hay que autorizar sus IP de origen en `allowedSources`, además de añadir su ruta. El destino de cada paquete se valida también de forma independiente. El modo simple conserva las opciones de conexión y pausa, pero aplica automáticamente sus permisos de red básicos.

El emisor debe configurar su propia IP local para que coincida con `virtualAddress` en el receptor. Cambiar la IP anunciada no modifica la autorización: una dirección diferente rechaza la sesión. No hay negociación que amplíe permisos a petición del remoto.

## Orquestación

La lista VPN sitúa primero los peers con transporte QUIC establecido. Los guardados o pendientes mantienen «Configurar VPN»; el descubrimiento y los intentos de transporte siguen siendo automáticos. «Conectar» y «Conectar todos» solo actúan sobre peers de confianza con QUIC establecido y capacidad remota LAN confirmada. La API aplica esa misma condición antes de activar el adaptador local.

La capacidad se consulta por el canal de control del transporte autenticado, sin abrir una sesión ni pedir autorización. El remoto confirma si tiene elevación, un adaptador activo o el servicio de adaptador disponible. Es independiente del perfil «administrador» de un contacto y de `autoAccept`. Se consulta cada cinco segundos, la respuesta caduca a los quince y se descarta al cerrar el transporte. Las respuestas se vinculan al protocolo y a un identificador de petición. Un peer antiguo que no responde queda con capacidad desconocida; actualizar ambos extremos permite habilitar la conexión.

[`VpnController`](../../QuicNet/Vpn/VpnController.cs) reconcilia cada segundo la configuración, el descubrimiento, la confianza y las sesiones reales. Abre el protocolo VPN, no solamente el transporte QUIC. Limita a cuatro los intentos simultáneos, aplica un plazo de 30 segundos para establecerlos y usa reintentos exponenciales de aproximadamente 2 a 60 segundos con variación aleatoria. El núcleo arbitra los intentos simultáneos de ambos extremos.

Para conectar sin intervención después de configurar los equipos:

1. Guardar o autorizar las identidades de los peers con el mecanismo existente de QuicNet.
2. Abrir «Configurar VPN» en el contacto, elegir su modo y seleccionar «Conectar al abrir y mantener conectada» en ambos extremos.
3. Guardar. Esta opción activa `autoConnect` y `autoAccept` para esa identidad y habilita la red. Las entradas con modo explícito deciden su inicio de forma independiente; las entradas antiguas sin modo siguen respetando el `autoConnect` global del archivo. Si falta elevación o el servicio de adaptador, se conserva la preferencia y aparece «Esperando permisos». Al abrir de nuevo la aplicación con permisos disponibles, se activa la red y se restauran las conexiones guardadas.

El editor funciona con el adaptador apagado y sin administrador. «Conectar manualmente» conserva el flujo con confirmación; «Solo aceptar automáticamente» permite que ese contacto inicie la VPN sin que este equipo lo busque para abrir una sesión. La pausa bloquea ambos sentidos. La elevación del proceso por sí sola no autoriza contactos nuevos.

Al seleccionar explícitamente LAN automática se utiliza la IP calculada para ese contacto y solo se autorizan su origen/ruta `/32`, acceso a la IP virtual local y broadcast. Los campos avanzados se conservan para volver a ese modo, pero sus rutas, orígenes adicionales y permisos de reenvío no se aplican en automático. El adaptador y el servicio reciben la unión de las políticas efectivas de los contactos habilitados.

## Tráfico en la interfaz

La tarjeta «Tráfico acumulado» superpone un historial RX/TX y muestra debajo las velocidades en bytes por segundo. El selector permite 30 segundos, 1 minuto o 5 minutos y recuerda el intervalo en este navegador. El historial se recoge durante esta sesión de interfaz, usa los contadores LAN del estado recibido por WebSocket y queda limitado a cinco minutos. Las velocidades se calculan con diferencias de contadores y tiempo transcurrido; las reconexiones, reinicios de contadores y pausas largas comienzan una nueva referencia para evitar picos artificiales. Si el estado deja de llegar, la velocidad aparece como no disponible.

La API `/api/vpn/config` devuelve la configuración en camelCase. El editor también admite respuestas PascalCase de versiones anteriores y convierte la identidad Base64 del estado de contactos a la huella hexadecimal de la política. Los errores al abrir y guardar son visibles; un guardado rechazado conserva el diálogo y no duplica la entrada al reintentarlo.

Los peers guardados con permiso automático VPN conservan ese comportamiento sin necesitar entradas explícitas en `vpn.json`; una entrada explícita tiene prioridad. La red local utiliza el descubrimiento existente para encontrar sus endpoints. Descubrir un dispositivo no autoriza su identidad.

La desconexión manual establece una pausa persistente y cierra la sesión. El remoto no puede reabrirla mientras esté pausada. Pulsar «Conectar» permite una conexión manual nueva; para reanudar también la reconexión automática, desmarcar «Pausar conexiones entrantes y salientes» y activar autoconexión en la configuración del peer. Desactivar la VPN revoca las sesiones y evita que una petición entrante reactive el adaptador. El controlador es quien prepara y recupera el adaptador; las peticiones entrantes no eluden sus reintentos.

La aplicación QuicNet debe estar en ejecución para descubrir peers y orquestar las sesiones. El servicio de adaptador arranca con el sistema, pero no sustituye al nodo QUIC ni ejecuta la interfaz de usuario.

## Archivo de usuario y recarga

Ejemplo del ordenador A, con IP `10.0.0.1`, conectado a B (`10.0.0.2`) y a la LAN `192.168.50.0/24` situada detrás de B:

```json
{
  "version": 1,
  "revision": 1,
  "mode": "advanced",
  "enabled": true,
  "autoConnect": true,
  "autoAssign": false,
  "localAddress": "10.0.0.1",
  "subnetMask": "255.255.255.0",
  "mtu": 1420,
  "peers": [
    {
      "certHash": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
      "enabled": true,
      "paused": false,
      "autoConnect": true,
      "autoAccept": true,
      "virtualAddress": "10.0.0.2",
      "allowedSources": ["192.168.50.0/24"],
      "accessibleDestinations": ["10.0.0.1/32"],
      "routes": ["192.168.50.0/24"],
      "allowBroadcast": false,
      "allowForwarding": false
    }
  ]
}
```

Sustituir la huella de ejemplo por la identidad real y previamente autorizada de B. En B, configurar su IP local como `10.0.0.2`, la IP del peer A como `10.0.0.1`, `accessibleDestinations: ["192.168.50.0/24"]` y `allowForwarding: true`. La red física necesita una ruta de retorno hacia la VPN o NAT configurado por el administrador. En Windows, el administrador debe preparar también el reenvío en la interfaz física si la usa como salida. QuicNet no crea NAT ni configura los firewalls de la LAN.

La asignación automática usa siempre máscara `/24` (`255.255.255.0`) y 250 direcciones de host, `10.0.0.2` a `10.0.0.251`. Al pasar a manual, la pantalla ofrece la IP actual como punto de partida. Los cambios de dirección requieren actualizar las expectativas de los receptores.

El archivo se vigila y recarga después de una breve espera para agrupar escrituras. Las escrituras de la interfaz son atómicas y usan `expectedRevision` para evitar sobrescribir otra edición. El tamaño máximo es 128 KiB. JSON inválido, máscaras no contiguas, versiones desconocidas y rutas ambiguas se rechazan. Un error de edición conserva la última configuración válida y aparece en la pantalla. Las colisiones de IP automática se notifican; hay que resolverlas con direcciones manuales únicas. No se cambia una IP silenciosamente después de anunciarla.

API local:

- `GET /api/vpn/config` devuelve configuración, revisión, ruta, errores, valores iniciales de los peers y estado de orquestación.
- `POST /api/vpn/config` recibe `{ "configuration": { ... }, "expectedRevision": 1 }`. Conserva las protecciones de origen y CSRF de la WebUI. Una revisión obsoleta se rechaza.
- Los endpoints LAN anteriores siguen disponibles y delegan en el controlador cuando existe.

## Servicio: uso diario sin elevación

Crear interfaces virtuales y modificar rutas requiere privilegios del sistema operativo. Se separan esas operaciones en un servicio que un administrador instala una vez; la aplicación normal se comunica con él usando un canal local autenticado.

En Windows se usa una named pipe restringida al SID autorizado y a SYSTEM. El cliente verifica que el proceso servidor sea el servicio registrado en SCM. En Linux se usa un socket Unix de propietario y modo restringidos, y se comprueban las credenciales del proceso en ambos extremos. El servicio admite un propietario y una sesión de adaptador simultánea. Sus binarios y política administrativa quedan protegidos contra escrituras del usuario normal.

El servicio acepta ajustes de interfaz, listas CIDR y paquetes IPv4. El cliente no puede elegir ejecutables, comandos, adaptadores ni archivos que el servicio deba ejecutar o cargar. El servicio vuelve a validar la política al abrir cada sesión y aplica un segundo filtro de paquetes antes de inyectarlos.

Publicar en una carpeta separada, por ejemplo:

```powershell
dotnet publish QuicNet/QuicNet.csproj -c Release --self-contained false -o publish-vpn
[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
```

Guardar el SID del usuario que ejecutará QuicNet. Desde una consola administrativa:

```powershell
.\scripts\install-vpn-broker.ps1 -PublishDirectory .\publish-vpn -OwnerSid 'S-1-5-21-...'
```

El script instala `QuicNetVpnBroker` bajo LocalSystem y copia los binarios a `%ProgramFiles%/QuicNet.Vpn`. Las publicaciones dependientes del framework requieren el runtime .NET correspondiente instalado para el sistema; se puede usar una publicación autónoma para distribuirlo junto al ejecutable. Cerrar la sesión elevada de QuicNet y abrir la aplicación como usuario normal después de instalar el servicio.

En Linux, publicar para Linux y ejecutar una vez como root:

```sh
sudo sh scripts/install-vpn-broker.sh /ruta/a/publish-vpn "$(id -u)"
```

El instalador crea la unidad `quicnet-vpn-broker.service`, los binarios en `/opt/quicnet-vpn` y la política en `/etc/quicnet/vpn-broker.json`. Requiere systemd, `iproute2`, acceso a `/dev/net/tun` y, si se autoriza reenvío, `sysctl`. El UID debe corresponder a una cuenta existente; reinstalar para otro usuario requiere editar primero el propietario de la política. La aplicación se abre después con el usuario normal. Para comprobar el servicio: `systemctl status quicnet-vpn-broker` y `journalctl -u quicnet-vpn-broker`.

### Implementación Linux

Compatibilidad verificada del backend x64 en Ubuntu 24.04, Arch rolling y Fedora 44: [matriz, dependencias y configuración del firewall](../audits/vpn-linux-distros-2026-10-18.md). Las pruebas en contenedores validan su espacio de usuario; SELinux/AppArmor, firewall y el servicio instalado requieren además comprobarse en un sistema completo.

Se usa el [TUN nativo del kernel](https://kernel.org/doc/html/latest/networking/tuntap.html) con `IFF_TUN | IFF_NO_PI`: entrega paquetes IP directamente al transporte QUIC existente. Mantiene la misma política, identidad y formato de paquetes de Windows. Una cola corresponde al único bucle de captura actual; multiqueue añadiría coordinación sin lectores adicionales que lo aprovechen.

Los descriptores no bloqueantes tienen `CLOEXEC` para no heredarlos en procesos `ip`/`sysctl` y están protegidos por `SafeFileHandle` durante cada operación. El cierre concurrente no puede reutilizar un descriptor que todavía está leyendo o esperando. `poll` limita la espera de cancelación/cierre a intervalos de 50 ms, sin asignaciones de memoria por paquete. Se reintenta `EINTR`; `EAGAIN`/`ENOBUFS` al inyectar descartan ese paquete por congestión sin derribar la sesión. EOF, errores permanentes y eventos de desconexión marcan la sesión como averiada para activar la recuperación existente.

La configuración usa `iproute2` con argumentos separados, diagnóstico de stderr y plazos limitados. Se comprueban nombres de interfaz, IPv4 y máscaras contiguas antes de ejecutar comandos. La [dirección broadcast se configura explícitamente](https://man7.org/linux/man-pages/man8/ip-address.8.html) para la LAN; cambiar la dirección retira la anterior. Se rechaza reutilizar un TUN existente y el dispositivo no persistente desaparece al cerrar el último descriptor.

El servicio conserva root para autenticar el servidor mediante `SO_PEERCRED`, pero systemd limita sus capacidades a `CAP_NET_ADMIN`, `CAP_CHOWN`, `CAP_DAC_OVERRIDE` y `CAP_FOWNER`, restringe familias de sockets y protege el sistema y los directorios personales. `CAP_CHOWN` permite entregar el socket Unix al UID autorizado. Una conexión con UID incorrecto se cierra sin detener el servicio. También se reconoce `CAP_NET_ADMIN` efectivo para ejecución directa; el servicio es el método recomendado para uso diario sin elevar la interfaz gráfica.

## Política administrativa

Ruta Windows: `%ProgramData%/QuicNet/vpn-broker.json`. Ruta Linux: `/etc/quicnet/vpn-broker.json`. El archivo lo puede editar el administrador. Por defecto limita el adaptador, las rutas y los destinos a la LAN virtual `10.0.0.0/24`, con reenvío desactivado. La aplicación puede restringir esos permisos, pero no ampliarlos.

Ejemplo para permitir además compartir la LAN anterior:

```json
{
  "owner": "S-1-5-21-...",
  "adapterName": "QuicPunchAdapter",
  "allowedAddresses": ["10.0.0.0/24"],
  "allowedRoutes": ["10.0.0.0/24", "192.168.50.0/24"],
  "allowedDestinations": ["10.0.0.0/24", "192.168.50.0/24"],
  "minimumPrefixLength": 24,
  "allowBroadcast": true,
  "allowForwarding": true
}
```

En Linux, `owner` es el UID numérico expresado como texto y el adaptador por defecto es `qp-tun0`. Los límites se comprueban sobre el prefijo completo: permitir una IP inicial no autoriza una red más amplia. `minimumPrefixLength` limita la amplitud de la red conectada de la interfaz. Un cambio de archivo administrativo cierra la sesión actual y vuelve a cargar la política; el controlador reconecta cuando el servicio esté disponible.

Para una salida por defecto, autorizar `0.0.0.0/0` en las rutas del cliente y los destinos del ordenador de salida, junto al reenvío y las rutas de retorno/NAT apropiados. Antes de instalar rutas amplias se preservan rutas `/32` por el enlace físico para los endpoints de transporte conocidos, evitando que QUIC y STUN se enruten a su propio túnel. Se limita esta lista a 256 endpoints y se notifica si se supera. No se reemplazan rutas ajenas; solo se retiran las rutas añadidas por la sesión.

En Windows la consulta de la ruta física usa [GetBestRoute de IP Helper](https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-getbestroute), evitando arrancar un proceso de PowerShell por cada endpoint. Se ha comprobado la consulta nativa sin modificar la tabla de rutas.

## Validación y límites actuales

El filtro de datos valida versión, longitud, checksum IPv4, origen autorizado, destino y sesión vigente en datagramas y en el fallback de stream. Rechaza las opciones IPv4 de source routing que podrían alterar el destino después de comprobar la ACL. Las rutas usan el prefijo más específico. Una ruta de un peer desconectado o pausado conserva su propietario y no cae sobre una salida por defecto de otro peer. Se rechazan orígenes que suplanten a otro peer o a las IP locales.

Un registro limitado de flujos permite respuestas TCP, UDP e ICMP echo a tráfico iniciado localmente, sin conceder acceso entrante general. Los cambios de política vacían ese registro. El reenvío entre peers requiere permiso explícito y reduce TTL; los broadcasts recibidos solo se entregan localmente para evitar bucles.

El túnel actual soporta IPv4. IPv6 no se anuncia ni se configura. El permiso automático de retorno no cubre fragmentos IPv4 ni errores ICMP; para esos casos se necesita una ACL de destino explícita. Una ruta `0.0.0.0/0` tampoco configura DNS ni desvía IPv6.

Pruebas reproducibles:

```sh
dotnet run --project QuicPunchTests -- --test-vpn-policy
dotnet run --project QuicPunchTests -- --test-vpn-orchestration
dotnet run --project QuicPunchTests -- --test-vpn-broker
node QuicPunchTests/Tests/VpnWebTests.mjs
```

Pruebas específicas de Linux, sin dependencias gráficas ni bibliotecas QUIC:

```sh
# Validaciones y E/S de sesión; no modifica interfaces del sistema.
dotnet run --project QuicPunch.Vpn.Tests
# Requiere Linux, .NET 11, iproute2, util-linux (unshare/runuser), nobody y sudo/root.
bash scripts/test-vpn-linux.sh
```

El segundo comando usa espacios de nombres privados de red y montaje, con `/run` temporal. Comprueba TUN real, UDP en ambos sentidos, broadcast, MTU, cambio de IP a `/32`, rutas por defecto y bypass físico de endpoints, reenvío y limpieza. También comprueba cancelación, cierre concurrente, congestión, fallos de captura y cero bytes administrados asignados por la E/S en 1000 pares lectura/escritura. Un cliente con UID `nobody`, sin `CAP_NET_ADMIN`, intercambia paquetes reales con el broker root; un UID incorrecto es rechazado. Validado en Ubuntu/WSL2 x64; las operaciones TUN/rutas también se ejecutaron correctamente bajo las restricciones systemd del instalador. Estas pruebas no equivalen a una prueba entre dos equipos Linux físicos ni instalan el servicio en el equipo.

La prueba de orquestación usa dos nodos QUIC reales y adaptadores simulados: comprueba conexión automática simultánea, stream y datagramas, rechazo de spoofing, pausa de ambos sentidos, recarga de políticas, interfaces manuales `/32`, respuestas autorizadas, recuperación de fallos de captura y desactivación. La prueba del broker comprueba el transporte local bidireccional, lecturas parciales y desconexiones, sin modificar interfaces ni rutas del sistema. En Windows, las pruebas QUIC pueden necesitar acceso al almacén de certificados para crear sus identidades temporales.

Migración: la IP automática anterior se sustituye por `10.0.0.x/24`. Actualizar ambos extremos o fijar IPs manuales compatibles. Las preferencias LAN existentes se importan al crear `vpn.json`; después, este archivo es la fuente de configuración de la VPN.
