# Verificación VPN: Ubuntu, Arch y Fedora

Fecha: 18 de octubre de 2026. Alcance: backend VPN Linux x64, `iproute2`, E/S TUN y broker Unix. No se ha validado la aplicación gráfica completa ni un enlace entre dos equipos físicos en cada distribución.

## Resultados ejecutados

| Entorno | glibc | iproute2 | Resultado |
| --- | --- | --- | --- |
| Ubuntu 24.04.5 LTS, imagen `ubuntu:24.04` | 2.39 | 6.1.0 | Correcto |
| Arch rolling, imagen `archlinux:base`, VERSION_ID `20261011.0.609700` | 2.44 | 7.2.0 | Correcto |
| Fedora 44, imagen `fedora:44` | 2.43 | 6.17.0 | Correcto |

Se ejecutaron contenedores temporales con red privada, `/run` temporal y publicaciones/runtime de solo lectura. Se usó el mismo código compilado de `QuicPunch.Vpn.Tests` y el runtime .NET 11 RC1 x64 de Ubuntu. Esto verifica distintas bibliotecas, herramientas y cuentas de usuario sobre el kernel compartido del anfitrión Docker; no equivale a probar los kernels, SELinux, AppArmor, NetworkManager, firewalls ni systemd nativos de tres instalaciones completas. Ubuntu 24.04 también se probó directamente en WSL, incluyendo TUN/rutas/reenvío bajo las restricciones systemd del instalador.

En los tres entornos pasaron:

- Captura e inyección UDP reales entre TUN y sockets del sistema; broadcast y MTU.
- Cambio de dirección a `/32`, creación/eliminación de rutas y limpieza de interfaces.
- Ruta por defecto con bypass físico de los endpoints del transporte.
- Activación/restauración del reenvío IPv4.
- Cancelación, cierre concurrente, detección de fallos, congestión y E/S sin asignaciones administradas en 1000 pares lectura/escritura.
- Autenticación Unix por UID, rechazo de otro UID, dos conexiones sucesivas de un cliente sin privilegios y limpieza del broker.
- Archivos y ajustes del instalador, conservación del propietario y llamada de reetiquetado SELinux. En esta prueba del instalador `systemctl` y `restorecon` son simulados; no se afirma haber iniciado el servicio ni aplicado una política SELinux real dentro del contenedor.

## Correcciones

El instalador crea explícitamente `/etc/systemd/system`, ausente en la imagen mínima de Fedora. Comprueba sus herramientas antes de resolver la publicación, incluye un `PATH` administrativo explícito en la unidad y ejecuta `restorecon` cuando está disponible. No desactiva SELinux ni AppArmor. Las pruebas consultan el UID de `nobody` en cada sistema en vez de presuponer un número.

La VPN no necesita implementaciones distintas por distribución: las llamadas TUN, `poll`, las credenciales de sockets Unix y los comandos utilizados pertenecen a interfaces comunes de Linux/iproute2. No depende de Netplan, `netctl`, `ifconfig` ni scripts de red específicos de Ubuntu.

## Dependencias de la VPN

Sobre una instalación convencional con glibc y systemd:

```sh
# Ubuntu
sudo apt install iproute2 procps util-linux systemd
# Arch (mantener el sistema actualizado al instalar paquetes)
sudo pacman -Syu --needed iproute2 procps-ng util-linux systemd
# Fedora Workstation/Server
sudo dnf install iproute procps-ng util-linux systemd policycoreutils
```

`ip` pertenece a [iproute2 en Ubuntu](https://packages.ubuntu.com/noble/iproute2), [iproute2 en Arch](https://archlinux.org/packages/core/x86_64/iproute2/) e [iproute en Fedora](https://packages.fedoraproject.org/pkgs/iproute/iproute/). `sysctl` solo es necesario al autorizar reenvío; las pruebas también usan `unshare`, `runuser` y la cuenta `nobody`. El kernel debe ofrecer `/dev/net/tun`; si falta y el controlador es modular, cargarlo con `sudo modprobe tun`.

El SDK .NET 11 es necesario para compilar las pruebas. La publicación Native AOT de la aplicación no necesita instalar ese SDK, pero conserva sus dependencias nativas y de escritorio. Esta matriz no valida todas las versiones históricas de las distribuciones, ARM, musl ni las variantes inmutables de Fedora.

## Firewalls y control de acceso

El tráfico autorizado por la política VPN se inyecta en TUN y después pasa por el firewall del equipo. Tener la interfaz activa no garantiza que un servicio TCP/UDP local acepte sus paquetes. [firewalld vincula interfaces a zonas](https://firewalld.org/documentation/man-pages/firewalld.zone.html); la configuración debe permitir los servicios deseados en `qp-tun0`.

Para una LAN de juegos que deba aceptar TCP/UDP en el adaptador VPN, un administrador puede crear una zona dedicada, conservando las zonas de las interfaces físicas:

```sh
# Solo crear la zona una vez. Cambiar qp-tun0 si la política administrativa utiliza otro nombre.
sudo firewall-cmd --permanent --new-zone=quicnet
sudo firewall-cmd --permanent --zone=quicnet --add-interface=qp-tun0
sudo firewall-cmd --permanent --zone=quicnet --add-protocol=tcp
sudo firewall-cmd --permanent --zone=quicnet --add-protocol=udp
sudo firewall-cmd --reload
```

Esos comandos permiten entrada TCP/UDP en esa interfaz; también se pueden autorizar únicamente puertos o servicios concretos. No crean NAT ni autorizan el reenvío entre zonas para el modo avanzado. El instalador no modifica automáticamente el firewall. Con UFW activo en Ubuntu o reglas nftables propias en Arch, aplicar la regla equivalente para los servicios de la interfaz virtual.

Fedora utiliza etiquetas SELinux; el instalador restaura las etiquetas predeterminadas de sus archivos usando [restorecon](https://fedoraproject.org/wiki/SELinux/ManagingFileContext). Una política local que confine el ejecutable puede necesitar permisos específicos para TUN, rutas y el socket Unix. Ubuntu también puede aplicar [perfiles AppArmor](https://documentation.ubuntu.com/server/how-to/security/apparmor/index.html). Las denegaciones se diagnostican con el journal/audit del sistema, conservando el control de acceso activo.

## Repetir la matriz

En un anfitrión Linux con Docker y SDK .NET 11:

```sh
bash scripts/test-vpn-distros.sh
# Una sola imagen
bash scripts/test-vpn-distros.sh fedora:44
# Prueba nativa del kernel y broker del sistema actual
bash scripts/test-vpn-linux.sh
```

La matriz también está definida en `.github/workflows/vpn-linux.yml`. Se ha comprobado localmente su Dockerfile y su entrada de pruebas con las tres imágenes; no se ha ejecutado una ronda remota de GitHub Actions en este trabajo. Los tags rolling cambian con el tiempo, de modo que la salida imprime las versiones utilizadas.

Para cerrar la validación de distribución completa quedan por ejecutar los mismos casos con el broker instalado bajo systemd, firewalld/SELinux en Fedora y las reglas de firewall/control de acceso de Ubuntu y Arch, además de un intercambio VPN entre dos equipos reales.
