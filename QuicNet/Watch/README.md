# QuicWatch

QuicWatch es el protocolo de reproducción conjunta de QuicNet, construido sobre QuicPunch. Comparte la identidad, los peers y la ventana de QuicNet. El dispositivo propietario conserva el archivo original y los demás participantes solicitan únicamente los rangos que necesita su reproductor. No hay recodificación, subida a un servidor ni pérdida de calidad.

## Ejecutar

```powershell
dotnet run --project QuicNet/QuicNet.csproj
```

Cada persona abre QuicNet y entra en la pestaña **QuicWatch**. El propietario conecta los dispositivos, crea la sala, elige el vídeo y asigna a cada participante permiso de espectador o de control.

## Diseño del MVP

- Canal de control prioritario para sala, reloj, permisos, pausa y saltos.
- Canal de medios de prioridad baja para rangos de hasta 1 MiB.
- Caché temporal de chunks verificados en memoria para reproducir y avanzar sin descargar antes el archivo completo.
- SHA-256 por rango recibido.
- El propietario es la autoridad del estado; los controladores envían peticiones que este confirma para todos.
- Corrección de deriva suave en el reproductor y salto exacto cuando la diferencia es grande.

Actualmente una sala tiene un único origen de vídeo y vive mientras está abierta la aplicación. La persistencia de salas, la recuperación tras desconexiones largas y la distribución de fragmentos entre espectadores quedan fuera de este primer MVP.

## Recursos e identidades

Cada sesión de medios admite hasta 20 solicitudes (cuatro en servicio y hasta 16 pendientes cuando los cuatro workers están ocupados). El proceso sirve como máximo 16 rangos simultáneos, con rangos de hasta 1 MiB. Una sesión que excede su capacidad se cierra sin cerrar el transporte compartido. Las peticiones tienen un plazo de 30 segundos y cada escritura de frame, 10 segundos. Cancelar, cerrar o reemplazar la sesión cancela y espera sus trabajos.

Los anuncios de medios solo crean metadatos. Se admiten medios de hasta 64 GiB, ocho cachés globales y cuatro por origen; cada caché retiene hasta 4 MiB y 1.024 chunks con expulsión LRU. Esto limita los datos retenidos a 32 MiB globales y 16 MiB por origen, además de los buffers transitorios de lectura y transferencia. No se reservan archivos de disco por anuncio. Cambiar de medio, abandonar o cerrar la sala retira las cachés anteriores y cancela sus lecturas. Al iniciar se limpian archivos heredados `GUID.cache` que no estén en uso.

La sala utiliza la identidad local asociada a su canal autenticado: WAN para participantes WAN y Tor para participantes Tor. Las invitaciones Tor deben dirigirse al ID Tor. Una sala pertenece a un único ámbito; para participantes WAN y Tor hay que crear salas separadas. La selección del medio, los permisos, el control y la salida conservan esa identidad.
