# Archivos temporales y claves

- `.artifacts/` es desechable: el script `scripts/clean-project.ps1` elimina su contenido completo. Solo guardar archivos temporales, cachés y resultados que puedan regenerarse.
- Nunca guardar claves privadas, keystores, credenciales, datos únicos ni la única copia de un archivo importante en `.artifacts/`, ni en otras carpetas de compilación o caché.
- Guardar la clave privada de publicación fuera del repositorio y del CDN. La ubicación predeterminada se obtiene con `Get-DefaultReleaseSigningKeyPath` en `scripts/ReleaseSecrets.psm1`: `%LOCALAPPDATA%/QuicNet/signing/release-private.pem` en Windows y la carpeta de datos local del usuario en Linux.
- Una clave de publicación perdida no se reemplaza generando otra: recuperar la identidad original y mantener una copia de seguridad privada. No mostrar el contenido de claves en registros ni respuestas.
- Usar las carpetas estándar `bin/` y `obj/` para compilar y probar. Las compilaciones de publicación separadas usan `bin/updates/`. No crear carpetas `.codex-*` por proyecto para cada prueba o tarea; las antiguas solo contienen resultados de compilación y el script de limpieza las retira cuando reconoce ese contenido. Conservar `.codex/` si contiene configuración o instrucciones.

## Localizar el cambio

- La interfaz compartida vive en `QuicNet.Shared/wwwroot/`. Sigue la vista y su JavaScript hasta el módulo de `QuicNet.Shared/WebUi/Modules/`. Verifica el contrato en `QuicNet.Shared/Serialization/` cuando cambien campos JSON.
- Los protocolos de aplicación viven en `QuicNet.Shared/Protocols/`. El motor P2P está en `QuicPunch/`. El arranque de escritorio está en `QuicNet/Program.cs` y el de Android en `QuicPunch.AndroidProbe/AndroidConnectionService.cs`.
- Usa `doc/architecture/development-map.md` para seguir el flujo y `doc/README.md` para localizar la guía de la función y `doc/testing/test-suite.md` para elegir las pruebas. Los ejemplos históricos y los archivos de `backups/` o `precached/` son material de referencia. Busca primero en el proyecto activo con `rg`.

## Comprobar el cambio

- Ejecuta los comandos desde la raíz. Usa el SDK de `global.json`. Para preparar las pruebas web en un checkout nuevo, usa `npm ci` y, si necesitas Chromium, `npm run test:browser:install`.
- JavaScript rápido · `npm run test:web`.
- Interfaz en Chromium · `node scripts/test-web.mjs --browser --list` para descubrir suites y `node scripts/test-web.mjs --browser --filter=ContactHome` como ejemplo de ejecución filtrada. Verifica escritorio y móvil cuando el cambio afecte a ambos.
- C# · Usa `./scripts/test.ps1 -Tests connection-modes` como ejemplo. Usa `dotnet run --project QuicPunchTests -c Test -- --list` para descubrir suites. El script compila una vez por invocación. Pasa las suites relevantes juntas. Usa `-NoBuild` en PowerShell solo con los binarios vigentes para el código que quieres probar.
- Usa pruebas aisladas o fixtures para red y medios cuando exista esa opción. El ejecutable limita `--loopback-only` a las suites compatibles. Consulta el log del fallo en `.artifacts/test-output/`. Al terminar, indica pruebas ejecutadas y plataformas pendientes.

## Contratos de QuicNet

- Descubrir un par permite considerarlo como candidato. La autorización de la aplicación se decide aparte. Conserva las comprobaciones de identidad y permiso por protocolo al cambiar conectividad o sesiones.
- Mantén compatibles las rutas HTTP, eventos WebSocket, JSON, datos persistidos e identificadores de protocolo mientras se migren sus consumidores. Si cambia un contrato JSON del servidor, comprueba su registro de serialización y la suite `aot-json`.
- Si cambias el ciclo de vida, verifica cancelación, cierre y recuperación del flujo afectado. Preserva la propiedad de los recursos y verifica los hosts que usan el código común.
