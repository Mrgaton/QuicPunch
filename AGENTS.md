# Archivos temporales y claves

- `.artifacts/` es desechable: el script `scripts/clean-project.ps1` elimina su contenido completo. Solo guardar archivos temporales, cachés y resultados que puedan regenerarse.
- Nunca guardar claves privadas, keystores, credenciales, datos únicos ni la única copia de un archivo importante en `.artifacts/`, ni en otras carpetas de compilación o caché.
- Guardar la clave privada de publicación fuera del repositorio y del CDN. La ubicación predeterminada se obtiene con `Get-DefaultReleaseSigningKeyPath` en `scripts/ReleaseSecrets.psm1`: `%LOCALAPPDATA%/QuicNet/signing/release-private.pem` en Windows y la carpeta de datos local del usuario en Linux.
- Una clave de publicación perdida no se reemplaza generando otra: recuperar la identidad original y mantener una copia de seguridad privada. No mostrar el contenido de claves en registros ni respuestas.
- Usar las carpetas estándar `bin/` y `obj/` para compilar y probar. Las compilaciones de publicación separadas usan `bin/updates/`. No crear carpetas `.codex-*` por proyecto para cada prueba o tarea; las antiguas solo contienen resultados de compilación y el script de limpieza las retira cuando reconoce ese contenido. Conservar `.codex/` si contiene configuración o instrucciones.
